using System;
using System.Collections.Generic;
using UnityEngine;
using CommNet;

namespace KerbalRadio
{
    public static class RadioPhysics
    {
        private const double SPEED_OF_LIGHT = 299792458.0;
        private const double BOLTZMANN = 1.380649e-23;
        private const double SYSTEM_TEMP = 290.0;
        public static LinkResult CalculateDirectLink(Vector3d vesselPos, Vector3d upAxis, ModuleRadioLink module)
        {
            return CheckDirectDSN(vesselPos, upAxis, module);
        }
        public static LinkResult CalculateBestLink(Vector3d vesselPos, Vector3d upAxis, ModuleRadioLink module)
        {
            LinkResult directResult = CheckDirectDSN(vesselPos, upAxis, module);

            if (directResult.hasSignal && directResult.snr > -10.0)
            {
                return directResult;
            }
            return CheckRelayCommNet(vesselPos, upAxis, module, directResult);
        }
        private static LinkResult CheckDirectDSN(Vector3d vesselPos, Vector3d upAxis, ModuleRadioLink module)
        {
            LinkResult result = new LinkResult();
            result.hasSignal = false;
            double bestRxPower = -9999;

            if (DsnManager.Stations == null || DsnManager.Stations.Count == 0) return result;

            foreach (DsnStation st in DsnManager.Stations)
            {
                CelestialBody stationBody = FlightGlobals.Bodies.Find(b => b.name == st.bodyName);
                if (stationBody == null) continue;

                Vector3d dsnPos = stationBody.GetWorldSurfacePosition(st.lat, st.lon, 250.0);

                bool isBlocked = false;
                foreach (CelestialBody cb in FlightGlobals.Bodies)
                {
                    if (!HasLineOfSight(vesselPos, dsnPos, cb))
                    {
                        isBlocked = true;
                        break;
                    }
                }

                if (isBlocked) continue;

                double distanceKm = Vector3d.Distance(vesselPos, dsnPos) / 1000.0;
                if (distanceKm < 1.0) distanceKm = 1.0;

                Vector3d directionToDsn = (dsnPos - vesselPos).normalized;
                double pointingError = Vector3d.Angle(upAxis, directionToDsn);

                double gainDrop = 0;
                if (pointingError > module.beamwidth / 2.0)
                {
                    gainDrop = (pointingError - module.beamwidth / 2.0) * 2.0;
                }

                double pathLoss = 20.0 * Math.Log10(distanceKm) + 20.0 * Math.Log10(module.frequencyMHz) + 32.44;
                double rxPower = module.transmitPower + module.antennaGain + st.gain - pathLoss - gainDrop;

                if (rxPower > bestRxPower)
                {
                    bestRxPower = rxPower;
                    result.dsnName = st.name;
                    result.dsnPos = dsnPos;
                    result.pointingError = pointingError;
                    result.ping = (distanceKm * 1000.0 / SPEED_OF_LIGHT) * 2.0;
                    result.hopCount = 1;
                    result.isOcculted = false;
                }
            }

            if (bestRxPower != -9999)
            {
                double bandwidthHz = module.bandwidthMHz * 1e6;
                double systemNoiseDbm = -185.0 + 10.0 * Math.Log10(bandwidthHz);
                result.snr = bestRxPower - systemNoiseDbm;
                result.hasSignal = true;

                if (result.snr > -10.0)
                {
                    double snrLinear = Math.Pow(10, result.snr / 10.0);
                    result.speed = (bandwidthHz * Math.Log(1 + snrLinear, 2)) / 1000.0;
                }
                else
                {
                    result.speed = 0;
                }
            }

            return result;
        }

        private static LinkResult CheckRelayCommNet(Vector3d vesselPos, Vector3d upAxis, ModuleRadioLink module, LinkResult fallbackDirect)
        {
            LinkResult result = new LinkResult();
            Vessel vessel = module.vessel;

            if (vessel.connection == null || vessel.connection.ControlPath == null || vessel.connection.ControlPath.Count == 0)
            {
                fallbackDirect.hasSignal = false;
                return fallbackDirect;
            }

            CommPath path = vessel.connection.ControlPath;

            if (path.Count <= 1)
            {
                fallbackDirect.hasSignal = false;
                return fallbackDirect;
            }

            result.hasSignal = true;
            result.isOcculted = false;
            result.hopCount = path.Count;

            double totalDistance = 0;
            double bottleneckSpeed = double.MaxValue;
            double bottleneckSNR = double.MaxValue;

            CelestialBody star = GetStar();
            CommNode homeNode = path[path.Count - 1].b;
            result.dsnName = homeNode.isHome ? homeNode.name : "Ретрантор";

            for (int i = 0; i < path.Count; i++)
            {
                CommLink link = path[i];
                Vector3d posA = link.a.position;
                Vector3d posB = link.b.position;
                bool isPlanetBlocked = false;
                foreach (CelestialBody cb in FlightGlobals.Bodies)
                {
                    if (!HasLineOfSight(posA, posB, cb))
                    {
                        isPlanetBlocked = true;
                        break;
                    }
                }

                if (isPlanetBlocked)
                {
                    result.hasSignal = false;
                    return result; 
                }

                double hopDistanceMeters = Vector3d.Distance(posA, posB);
                totalDistance += hopDistanceMeters;

                double solarPenalty = 0;
                if (star != null)
                {
                    solarPenalty = CalculateSolarPenalty(posA, posB, star);
                    if (solarPenalty > 500)
                    {
                        result.hasSignal = false;
                        result.isOcculted = true;
                        return result;
                    }
                }

                double hopSNR = -999;
                double hopSpeed = 0;

                if (i == 0)
                {
                    Vector3d directionToTarget = (posB - posA).normalized;
                    result.pointingError = Vector3d.Angle(upAxis, directionToTarget);

                    double gainDrop = 0;
                    if (result.pointingError > module.beamwidth / 2.0)
                    {
                        gainDrop = (result.pointingError - module.beamwidth / 2.0) * 2.0;
                    }

                    double rxGain = link.b.isHome ? 74.0 : 38.0;
                    double hopDistanceKm = hopDistanceMeters / 1000.0;
                    if (hopDistanceKm < 1.0) hopDistanceKm = 1.0;

                    double pathLoss = 20.0 * Math.Log10(hopDistanceKm) + 20.0 * Math.Log10(module.frequencyMHz) + 32.44;
                    double rxPowerDBm = module.transmitPower + module.antennaGain + rxGain - pathLoss - gainDrop - solarPenalty;

                    double bandwidthHz = module.bandwidthMHz * 1e6;
                    double systemNoiseDbm = -185.0 + 10.0 * Math.Log10(bandwidthHz);

                    hopSNR = rxPowerDBm - systemNoiseDbm;

                    if (hopSNR > -10.0)
                    {
                        double snrLinear = Math.Pow(10, hopSNR / 10.0);
                        hopSpeed = (bandwidthHz * Math.Log(1 + snrLinear, 2)) / 1000.0;
                    }
                }
                else
                {
                    double signalStrength = link.signalStrength;
                    hopSNR = (signalStrength * 50.0) - 10.0 - solarPenalty;

                    double fakeNoiseW = BOLTZMANN * SYSTEM_TEMP * 1e6;
                    double rxPowerW = Math.Pow(10.0, hopSNR / 10.0) * fakeNoiseW;

                    if (rxPowerW > 0)
                    {
                        hopSpeed = 1e6 * Math.Log(1.0 + (rxPowerW / fakeNoiseW), 2) / 1000.0;
                    }
                }

                if (hopSpeed < bottleneckSpeed) bottleneckSpeed = hopSpeed;
                if (hopSNR < bottleneckSNR) bottleneckSNR = hopSNR;
            }

            if (bottleneckSNR <= -10.0) result.hasSignal = false;

            result.snr = bottleneckSNR;
            result.speed = (bottleneckSpeed == double.MaxValue) ? 0 : bottleneckSpeed;
            result.ping = (totalDistance / SPEED_OF_LIGHT) * 2.0;

            return result;
        }

        private static bool HasLineOfSight(Vector3d A, Vector3d B, CelestialBody body)
        {
            Vector3d C = body.position;
            Vector3d AB = B - A;
            Vector3d AC = C - A;

            double abSqrLength = Vector3d.Dot(AB, AB);
            if (abSqrLength < 1.0) return true;

            double t = Vector3d.Dot(AC, AB) / abSqrLength;
            if (t > 0.01 && t < 0.99)
            {
                Vector3d closestPoint = A + AB * t;
                double distanceToCenter = Vector3d.Distance(closestPoint, C);
                if (distanceToCenter < (body.Radius - 2500.0)) return false;
            }
            return true;
        }

        private static CelestialBody GetStar()
        {
            foreach (var body in FlightGlobals.Bodies)
            {
                if (body.isStar) return body;
            }
            return null;
        }

        private static double CalculateSolarPenalty(Vector3d posA, Vector3d posB, CelestialBody star)
        {
            Vector3d ray = posB - posA;
            Vector3d toStar = star.position - posA;

            double angleToStar = Vector3d.Angle(ray, toStar);
            double distanceToStar = toStar.magnitude;

            double starAngularRadius = Math.Asin(star.Radius / distanceToStar) * (180.0 / Math.PI);

            if (angleToStar < starAngularRadius) return 1000.0;
            else if (angleToStar < starAngularRadius * 3.0)
            {
                double fraction = (angleToStar - starAngularRadius) / (starAngularRadius * 2.0);
                return 50.0 * (1.0 - fraction);
            }
            return 0.0;
        }
    }
}