// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using CommNet;
using UnityEngine;

namespace RealisticRadio
{
    // Один участок линии по шагам: передатчик -> трасса -> приёмник -> C/N0 -> скорость. Все величины в дБ; что именно
    // отняло сигнал, видно по полям - окно «Линия связи» показывает их как есть
    public class HopBudget
    {
        public string from = "", to = "";
        public string feed = "";                  // облучатель станции, через который идёт сигнал
        public bool toGround, fromGround;         // приёмник (передатчик) - наземная станция
        public double frequencyMHz, bandwidthHz;

        // Передатчик
        public double txPowerDbm, txGainDbi, txOffAxisDeg, txPointingLossDb, txMatchingLossDb;

        // Трасса
        public double distanceM, pathLossDb, horizonLossDb, solarLossDb, atmosphereLossDb;
        public double elevationDeg = double.NaN;  // угол места аппарата над горизонтом станции
        public string blocked = "";               // почему участка нет (за горизонтом, нет передатчика...)
        public CelestialBody blocker;             // тело, закрывшее линию (null - открыта)

        // Приёмник
        public double rxGainDbi, rxFeedLossDb, rxOffAxisDeg, rxPointingLossDb, noiseK;
        public double noiseOwnK, noiseSkyK, noiseBodiesK, noiseAtmosphereK; // из чего сложен шум приёмника, К
        public string noiseBodies = "";                                       // какие тела шумят в луче

        // Итог
        public double rateKbps;
        public string mode = "";

        public double EirpDbm { get { return txPowerDbm - txMatchingLossDb + txGainDbi - txPointingLossDb; } }
        public double RxPowerDbm { get { return EirpDbm - pathLossDb - horizonLossDb - solarLossDb - atmosphereLossDb + rxGainDbi - rxFeedLossDb - rxPointingLossDb; } }
        public double GOverT { get { return rxGainDbi - rxFeedLossDb - 10.0 * Math.Log10(Math.Max(noiseK, 1.0)); } }
        public double Cn0 { get { return blocked.Length > 0 ? double.NegativeInfinity : RxPowerDbm - RadioMath.NoiseDensityDbm(Math.Max(noiseK, 1.0)); } }
        public double Margin { get { return Cn0 - RadioMath.MIN_LINK_CN0; } }   // запас до самой медленной линии, дБ
        public bool Usable { get { return blocked.Length == 0 && rateKbps > 0; } }
        public double DelaySeconds { get { return distanceM / RadioMath.SPEED_OF_LIGHT; } }
    }

    // Связь аппарата с Землёй целиком: путь вниз (данные и телеметрия), команды вверх и все станции для сравнения
    public class LinkReport
    {
        public readonly List<HopBudget> down = new List<HopBudget>();     // участки от аппарата до Земли
        public readonly List<HopBudget> up = new List<HopBudget>();       // участки от Земли до аппарата (команды)
        public readonly List<HopBudget> stations = new List<HopBudget>(); // прямая линия до каждой станции
        public bool viaRelay;
        public bool connected;                                            // есть путь CommNet до станции мода
        public bool stock;                                                // путь держит антенна без мода (до штатной станции)
        public bool visible;                                              // хоть одна станция или ретранслятор в прямой видимости
        public string why = "";                                           // почему связи нет

        public double RateKbps
        {
            get
            {
                if (down.Count == 0) return 0;
                double rate = double.MaxValue;
                foreach (HopBudget h in down) rate = Math.Min(rate, h.Usable ? h.rateKbps : 0.0);
                return rate;
            }
        }

        public HopBudget Bottleneck { get { return Weakest(down); } }
        public HopBudget UplinkBottleneck { get { return Weakest(up); } }
        public bool Downlink { get { return RateKbps > 0; } }
        public bool Uplink { get { return up.Count > 0 && up.TrueForAll(h => h.Usable); } }

        public double DelaySeconds
        {
            get
            {
                double s = 0;
                foreach (HopBudget h in down) s += h.DelaySeconds;
                return s;
            }
        }

        private static HopBudget Weakest(List<HopBudget> hops)
        {
            HopBudget weakest = null;
            foreach (HopBudget h in hops)
            {
                if (weakest == null || h.Cn0 < weakest.Cn0) weakest = h;
            }
            return weakest;
        }
    }

    // Конец линии: где он и на каком теле стоит. groundAltitude - высота земли под ним (NaN - высоко над телом): по ней
    // считается, что закрывает выпуклость тела
    public struct LinkEnd
    {
        public Vector3d position;
        public CelestialBody body;
        public double groundAltitude;
    }

    // Радио аппарата для расчёта: линия DSN детали (ModuleRadioLink) и антенна той же детали. У выгруженного аппарата -
    // образец детали с сохранённым состоянием раскрытия; куда смотрит его антенна, неизвестно - считаем наведённой
    public class LinkRadio
    {
        public Vessel vessel;
        public Part part;                        // null - аппарат выгружен
        public ModuleRadioLink link;
        public ModuleRadioAntenna antenna;
        public string name = "";
        public bool deployed = true;
        public Vector3d position;

        public LinkEnd End { get { return LinkCalc.EndOf(vessel, position); } }

        public double FrequencyMHz { get { return link.frequencyMHz; } }
        public double BandwidthHz { get { return Math.Max(link.bandwidthMHz * 1e6, 1.0); } }
        public bool Relay { get { return link.isRelay; } }

        // На сколько ось антенны отходит от направления: поворотное устройство наводит само, у выгруженного - неизвестно
        public double OffAxisDeg(Vector3d toward)
        {
            if (part == null || (antenna != null && antenna.tracking)) return 0.0;
            return Vector3d.Angle(part.transform.up, toward);
        }

        public double TxGainDbi(double f)
        {
            return antenna != null && antenna.IsDish ? antenna.GainAt(f) : link.antennaGain;
        }

        public double BeamDeg(double f)
        {
            return antenna != null && antenna.IsDish ? antenna.BeamwidthAt(f) : link.beamwidth;
        }

        // Приём на частоте f: потери согласования антенны и входной фильтр приёмника (чужой диапазон не слышен)
        public double RxLossDb(double f)
        {
            if (antenna != null) return antenna.LinkReceiveLossDb(f);
            return RadioMath.BandFilterLoss(f, link.frequencyMHz * 0.97, link.frequencyMHz * 1.03);
        }

        // Куда смотрит антенна: поворотное устройство и выгруженный аппарат - на собеседника, иначе по оси детали
        public Vector3d Boresight(Vector3d toward)
        {
            if (part == null || (antenna != null && antenna.tracking)) return toward;
            return part.transform.up;
        }

        // Собственный шум приёмного тракта без неба, К
        public double OwnNoiseK { get { return antenna != null && antenna.noiseTemperature > 0 ? antenna.noiseTemperature : LinkCalc.DEFAULT_RX_NOISE_K; } }
    }

    // Единый расчёт связи ядра: прямая линия до станций, участки между аппаратами, команды с Земли
    public static class LinkCalc
    {
        public const double ONBOARD_NOISE_K = 600.0;        // оценка шума командного приёмника, когда считать некогда (планировщик)
        public const double DEFAULT_RX_NOISE_K = 150.0;     // приёмный тракт аппарата без криогеники
        private const double EARTH_PRESSURE_KPA = 101.325;
        public const double COMMAND_BANDWIDTH_HZ = 4000.0;  // командная линия: до ~2 кбит/с
        public const double BLOCKED_LOSS_DB = 60.0;         // за горизонтом глубже этого - участка нет
        private const float RATES_REFRESH_S = 1f;           // скорости для CommNet пересчитываются раз в секунду
        private const double LOW_ALTITUDE_SHARE = 0.05;     // ниже этой доли радиуса аппарат «у поверхности»: горизонт по земле под ним
        public const double STATION_MAST_M = 20.0;          // антенна станции над землёй
        private const double CORONA_REACH_RADII = 12.0;     // в X-диапазоне корона мешает ближе 12 радиусов Солнца (~3° от него у Земли)
        private const double CORONA_REFERENCE_MHZ = 8420.0;

        // --- Радио аппарата ---

        public static LinkRadio RadioOf(ModuleRadioLink module)
        {
            Part p = module.part;
            return new LinkRadio
            {
                vessel = module.vessel,
                part = p,
                link = module,
                antenna = p != null ? p.FindModuleImplementing<ModuleRadioAntenna>() : null,
                name = Name(module.vessel, p != null && p.partInfo != null ? p.partInfo.title : ""),
                deployed = module.CanComm(),
                position = p != null ? (Vector3d)p.transform.position : module.vessel.GetWorldPos3D()
            };
        }

        // Все линии DSN аппарата: у загруженного - детали, у выгруженного - образцы деталей и их сохранённое состояние
        public static List<LinkRadio> RadiosOf(Vessel v)
        {
            var list = new List<LinkRadio>();
            if (v == null) return list;
            if (v.loaded)
            {
                foreach (ModuleRadioLink m in v.FindPartModulesImplementing<ModuleRadioLink>()) list.Add(RadioOf(m));
                return list;
            }
            if (v.protoVessel == null) return list;
            Vector3d position = v.GetWorldPos3D();
            foreach (ProtoPartSnapshot pps in v.protoVessel.protoPartSnapshots)
            {
                Part prefab = pps.partInfo != null ? pps.partInfo.partPrefab : null;
                if (prefab == null) continue;
                ModuleRadioLink link = prefab.FindModuleImplementing<ModuleRadioLink>();
                if (link == null) continue;
                list.Add(new LinkRadio
                {
                    vessel = v,
                    link = link,
                    antenna = prefab.FindModuleImplementing<ModuleRadioAntenna>(),
                    name = Name(v, pps.partInfo.title),
                    deployed = Deployed(pps),
                    position = position
                });
            }
            return list;
        }

        private static string Name(Vessel v, string part)
        {
            string vessel = v != null ? v.vesselName : "";
            return string.IsNullOrEmpty(part) ? vessel : $"{vessel} ({part})";
        }

        // Раскрыта ли антенна выгруженного аппарата: по сохранённому состоянию раскладного модуля
        private static bool Deployed(ProtoPartSnapshot pps)
        {
            foreach (ProtoPartModuleSnapshot m in pps.modules)
            {
                if (m.moduleName != "ModuleDeployableAntenna") continue;
                string state = m.moduleValues != null ? m.moduleValues.GetValue("deployState") : null;
                return state == null || state == "EXTENDED";
            }
            return true;
        }

        // --- Стороны участка: передатчик и приёмник числами ---
        // Участок считается одной функцией Hop. Ядро собирает стороны из линий DSN деталей и станций, дополнение - из своих
        // радиоблоков и антенн, а трасса, горизонт, Солнце, C/N0 и скорость у всех одни

        public class TxSide
        {
            public string name = "", feed = "", blocked = "";
            public LinkEnd end;
            public bool ground;                          // наземная станция
            public double frequencyMHz, bandwidthHz = 1.0;
            public double powerDbm, gainDbi, matchingLossDb, offAxisDeg, pointingLossDb;
            public double elevationDeg = double.NaN;     // у станции: угол места другого конца
        }

        public class RxSide
        {
            public string name = "", feed = "", blocked = "";
            public LinkEnd end;
            public bool ground;
            public double gainDbi, feedLossDb, offAxisDeg, pointingLossDb, noiseK;
            public double noiseOwnK, noiseSkyK, noiseBodiesK, noiseAtmosphereK;
            public string noiseBodies = "";
            public double elevationDeg = double.NaN;

            // Шум приёмника из источников: свой тракт + небо + тела в луче + атмосфера над наземной антенной
            public void SetNoise(double ownK, Vector3d boresight, double beamDeg, double f)
            {
                noiseOwnK = ownK;
                noiseSkyK = RadioMath.SkyNoise(f);
                noiseBodiesK = BodiesNoiseK(end, boresight, gainDbi, beamDeg, f, out noiseBodies);
                noiseAtmosphereK = AtmosphereNoiseK(end, boresight, f);
                noiseK = noiseOwnK + noiseSkyK + noiseBodiesK + noiseAtmosphereK;
            }
        }

        // Участок по шагам: передатчик -> трасса -> приёмник -> C/N0 и скорость. Почему участка нет - первое по порядку:
        // передатчик (антенна сложена, нет передатчика), трасса (тело, Солнце), приёмник (не слышит частоту)
        public static HopBudget Hop(TxSide tx, RxSide rx)
        {
            var h = new HopBudget
            {
                from = tx.name, to = rx.name, frequencyMHz = tx.frequencyMHz, bandwidthHz = tx.bandwidthHz,
                fromGround = tx.ground, toGround = rx.ground, feed = rx.ground ? rx.feed : tx.feed,
                txPowerDbm = tx.powerDbm, txGainDbi = tx.gainDbi, txMatchingLossDb = tx.matchingLossDb,
                txOffAxisDeg = tx.offAxisDeg, txPointingLossDb = tx.pointingLossDb,
                rxGainDbi = rx.gainDbi, rxFeedLossDb = rx.feedLossDb, rxOffAxisDeg = rx.offAxisDeg, rxPointingLossDb = rx.pointingLossDb,
                noiseK = rx.noiseK, noiseOwnK = rx.noiseOwnK, noiseSkyK = rx.noiseSkyK, noiseBodiesK = rx.noiseBodiesK,
                noiseAtmosphereK = rx.noiseAtmosphereK, noiseBodies = rx.noiseBodies,
                elevationDeg = rx.ground ? rx.elevationDeg : tx.elevationDeg,
                blocked = tx.blocked
            };
            Path(h, tx.end, rx.end);
            if (h.blocked.Length == 0) h.blocked = rx.blocked;
            Finish(h);
            return h;
        }

        // Передатчик линии DSN детали в сторону toward
        public static TxSide VesselTx(LinkRadio radio, Vector3d toward)
        {
            double f = radio.FrequencyMHz, vswr;
            var tx = new TxSide
            {
                name = radio.name, end = radio.End, frequencyMHz = f, bandwidthHz = radio.BandwidthHz,
                powerDbm = radio.link.transmitPower, gainDbi = radio.TxGainDbi(f),
                matchingLossDb = radio.antenna != null ? radio.antenna.MatchingLossDb(f, out vswr) : 0.0,
                offAxisDeg = radio.OffAxisDeg(toward - radio.position)
            };
            tx.pointingLossDb = RadioMath.PointingLoss(tx.offAxisDeg, radio.BeamDeg(f), tx.gainDbi);
            if (!radio.deployed) tx.blocked = "антенна сложена или выключена";
            return tx;
        }

        // Приём антенной детали на частоте f со стороны from
        public static RxSide VesselRx(LinkRadio radio, double f, Vector3d from)
        {
            var rx = new RxSide
            {
                name = radio.name, end = radio.End, gainDbi = radio.TxGainDbi(f), feedLossDb = radio.RxLossDb(f),
                offAxisDeg = radio.OffAxisDeg(from - radio.position)
            };
            rx.SetNoise(radio.OwnNoiseK, radio.Boresight(from - radio.position), radio.BeamDeg(f), f);
            rx.pointingLossDb = RadioMath.PointingLoss(rx.offAxisDeg, radio.BeamDeg(f), rx.gainDbi);
            if (!radio.deployed) rx.blocked = "антенна приёмника сложена или выключена";
            else if (rx.feedLossDb > 20.0) rx.blocked = $"приёмник не слышит {StationAntenna.Freq(f)}";
            return rx;
        }

        public static Vector3d StationPosition(DsnStation st, CelestialBody body)
        {
            return body.GetWorldSurfacePosition(st.lat, st.lon, st.altitude);
        }

        // Конец линии на станции: антенна на опоре над землёй
        public static LinkEnd StationEnd(DsnStation st, CelestialBody body)
        {
            return new LinkEnd { position = StationPosition(st, body), body = body, groundAltitude = st.altitude - STATION_MAST_M };
        }

        // Приём станцией: облучатель для этой частоты; ниже допустимого угла места антенна не опускается - аппарат у
        // горизонта слышен краем луча
        public static RxSide StationRx(DsnStation st, CelestialBody body, double f, Vector3d from)
        {
            LinkEnd end = StationEnd(st, body);
            var rx = new RxSide { name = st.name, end = end, ground = true, elevationDeg = Elevation(body, end.position, from) };
            StationAntenna feed = st.MainAntenna(f);
            if (feed == null)
            {
                rx.blocked = "у станции нет антенн";
                return rx;
            }
            rx.feed = string.IsNullOrEmpty(feed.title) ? StationAntenna.Range(feed.minMHz, feed.maxMHz) : feed.title;
            rx.gainDbi = feed.GainAt(f);
            rx.feedLossDb = feed.ReceiveLossDb(f);
            rx.SetNoise(feed.noiseTemperature, from - end.position, feed.BeamwidthAt(f), f);   // антенна станции смотрит на аппарат
            rx.pointingLossDb = LowElevationLoss(st, feed, f, rx.elevationDeg, out rx.offAxisDeg);
            if (rx.feedLossDb > 20.0) rx.blocked = $"станция не слышит {StationAntenna.Freq(f)} (слышит: {st.ReceiveBands})";
            return rx;
        }

        // Передатчик станции (команды): облучатель с передатчиком в диапазоне аппарата - его парная полоса
        public static TxSide StationTx(DsnStation st, CelestialBody body, double f, Vector3d to, double bandwidthHz)
        {
            LinkEnd end = StationEnd(st, body);
            var tx = new TxSide { name = st.name, end = end, ground = true, frequencyMHz = f, bandwidthHz = bandwidthHz, elevationDeg = Elevation(body, end.position, to) };
            StationAntenna feed = st.UplinkAntenna(f);
            if (feed == null)
            {
                tx.blocked = $"нет передатчика на {StationAntenna.Freq(f)} (передатчики: {st.TransmitBands})";
                return tx;
            }
            double vswr;
            tx.feed = string.IsNullOrEmpty(feed.title) ? StationAntenna.Range(feed.minMHz, feed.maxMHz) : feed.title;
            tx.powerDbm = 10.0 * Math.Log10(feed.uplinkW * 1000.0);
            tx.gainDbi = feed.GainAt(f);
            tx.matchingLossDb = feed.MatchingLossDb(f, out vswr);
            tx.pointingLossDb = LowElevationLoss(st, feed, f, tx.elevationDeg, out tx.offAxisDeg);
            return tx;
        }

        // Ниже допустимого угла места антенна станции не опускается: цель у горизонта видна краем луча, дБ
        public static double LowElevationLoss(DsnStation st, StationAntenna feed, double f, double elevationDeg, out double offAxisDeg)
        {
            offAxisDeg = 0.0;
            if (feed == null || !feed.Directional || !(elevationDeg < st.minElevation)) return 0.0;
            offAxisDeg = st.minElevation - elevationDeg;
            return RadioMath.PointingLoss(offAxisDeg, feed.BeamwidthAt(f), feed.GainAt(f));
        }

        // --- Участки ядра ---

        // Аппарат -> наземная станция
        public static HopBudget ToStation(LinkRadio tx, DsnStation st, CelestialBody body)
        {
            Vector3d sp = StationPosition(st, body);
            return Hop(VesselTx(tx, sp), StationRx(st, body, tx.FrequencyMHz, tx.position));
        }

        // Наземная станция -> аппарат: команды
        public static HopBudget FromStation(DsnStation st, CelestialBody body, LinkRadio rx)
        {
            Vector3d sp = StationPosition(st, body);
            double f = rx.FrequencyMHz;
            return Hop(StationTx(st, body, f, rx.position, COMMAND_BANDWIDTH_HZ), VesselRx(rx, f, sp));
        }

        // Аппарат -> аппарат на частоте передатчика
        public static HopBudget Between(LinkRadio tx, LinkRadio rx)
        {
            double f = tx.FrequencyMHz;
            return Hop(VesselTx(tx, rx.position), VesselRx(rx, f, tx.position));
        }

        // Трасса: потери в пространстве, тела на пути (у самой поверхности - дифракция за горизонт) и Солнце в луче приёмника
        private static void Path(HopBudget h, LinkEnd a, LinkEnd b)
        {
            double wavelength = RadioMath.Wavelength(h.frequencyMHz);
            h.distanceM = Vector3d.Distance(a.position, b.position);
            h.pathLossDb = RadioScale.PathLossDb(h.distanceM, h.frequencyMHz);   // как на настоящем расстоянии
            string why;
            h.horizonLossDb = Obstruction(a, b, wavelength, out why, out h.blocker);
            if (why.Length > 0 && h.blocked.Length == 0) h.blocked = why;
            h.solarLossDb = SolarLoss(a.position, b.position, h.frequencyMHz);
            if (h.solarLossDb >= BLOCKED_LOSS_DB && h.blocked.Length == 0) h.blocked = "сигнал идёт через солнечную корону";
            h.atmosphereLossDb = AtmosphereLossDb(a, b.position, h.frequencyMHz) + AtmosphereLossDb(b, a.position, h.frequencyMHz);
        }

        private static void Finish(HopBudget h)
        {
            if (h.blocked.Length > 0)
            {
                h.rateKbps = 0;
                h.mode = "";
                return;
            }
            h.rateKbps = RadioMath.LinkRateKbps(h.Cn0, h.bandwidthHz, out h.mode);
        }

        // --- Геометрия ---

        // Угол места точки target над горизонтом точки на теле, градусы
        public static double Elevation(CelestialBody body, Vector3d site, Vector3d target)
        {
            Vector3d up = (site - body.position).normalized;
            return 90.0 - Vector3d.Angle(up, target - site);
        }

        // Конец линии на аппарате: на поверхности или низко над ней - уровень земли под ним (ровер на плато в 2 км над
        // уровнем моря закрыт плато, а не уровнем моря), высоко - выпуклость тела по его среднему радиусу
        public static LinkEnd EndOf(Vessel v, Vector3d position)
        {
            var end = new LinkEnd { position = position, body = v != null ? v.mainBody : null, groundAltitude = double.NaN };
            if (v == null || v.mainBody == null) return end;
            if (v.LandedOrSplashed || v.situation == Vessel.Situations.PRELAUNCH) end.groundAltitude = v.altitude - 2.0;
            else if (v.altitude < LOW_ALTITUDE_SHARE * v.mainBody.Radius) end.groundAltitude = v.heightFromTerrain >= 0 ? v.altitude - v.heightFromTerrain : 0.0;
            if (v.mainBody.ocean && end.groundAltitude < 0) end.groundAltitude = 0.0;
            return end;
        }

        // Что лежит между a и b. Каждое тело - шар: если прямая проходит ниже его поверхности на глубину h, сигнал
        // огибает выпуклость (дифракция по ν), а глубже BLOCKED_LOSS_DB - участка нет. Шар тела, на котором стоит конец
        // линии, - по уровню земли под этим концом. Так ровер на обратной стороне Марса Землю не видит, станция на ночной
        // стороне Земли не слышит Марс, а соседний ровер в паре километров слышен с небольшими потерями
        public static double Obstruction(LinkEnd a, LinkEnd b, double wavelength, out string why)
        {
            CelestialBody blocker;
            return Obstruction(a, b, wavelength, out why, out blocker);
        }

        // То же и какое тело закрыло линию (null - открыта)
        public static double Obstruction(LinkEnd a, LinkEnd b, double wavelength, out string why, out CelestialBody blocker)
        {
            why = "";
            blocker = null;
            double loss = 0.0;
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null) return 0.0;
            foreach (CelestialBody body in bodies)
            {
                double l = BodyLoss(a.position, b.position, body.position, GroundRadius(body, a, b), wavelength);
                if (l >= BLOCKED_LOSS_DB)
                {
                    why = $"закрывает {body.bodyName}";
                    blocker = body;
                    return l;
                }
                loss += l;
            }
            return loss;
        }

        // Потери на одном теле-шаре (центр c, радиус земли radius) для линии a-b, дБ; BLOCKED_LOSS_DB и больше - закрыто
        public static double BodyLoss(Vector3d a, Vector3d b, Vector3d c, double radius, double wavelength)
        {
            Vector3d ab = b - a;
            double length2 = Vector3d.Dot(ab, ab);
            if (length2 < 1.0) return 0.0;
            double t = Vector3d.Dot(c - a, ab) / length2;
            if (t <= 0.0 || t >= 1.0) return 0.0;      // ближайшая к центру точка - конец линии: тело позади
            double length = Math.Sqrt(length2);
            double closest = Vector3d.Distance(a + ab * t, c);
            if (closest - radius > length) return 0.0; // далеко от линии
            double depth = radius - closest;           // насколько прямая проходит ниже земли
            double nu = RadioMath.FresnelNu(depth, t * length, (1.0 - t) * length, wavelength);
            return nu <= -0.78 ? 0.0 : RadioMath.DiffractionLossNu(nu, true);
        }

        private static double GroundRadius(CelestialBody body, LinkEnd a, LinkEnd b)
        {
            double radius = double.NaN;
            if (a.body == body && !double.IsNaN(a.groundAltitude)) radius = body.Radius + a.groundAltitude;
            if (b.body == body && !double.IsNaN(b.groundAltitude))
            {
                double rb = body.Radius + b.groundAltitude;
                radius = double.IsNaN(radius) ? rb : Math.Min(radius, rb);
            }
            return double.IsNaN(radius) ? body.Radius : radius;
        }

        // Солнечная корона: сигнал, прошедший близко к Солнцу, дробит и размывает плазма. Важно, на сколько радиусов Солнца
        // к нему подходит сама линия: в X-диапазоне связь хуже ближе ~12 радиусов (~3° от Солнца у Земли), к 4 радиусам
        // теряется ~30 дБ; на низких частотах корона мешает дальше (S - до ~23 радиусов), на Ka - ближе. Шум самого Солнца
        // в луче приёмника - отдельно, в шуме приёмника
        public static double SolarLoss(Vector3d a, Vector3d b, double frequencyMHz)
        {
            CelestialBody sun = Sun;
            return sun != null ? SolarLoss(a, b, sun.position, sun.Radius, frequencyMHz) : 0.0;
        }

        // То же при заданном положении Солнца (планировщику - в будущий момент)
        public static double SolarLoss(Vector3d a, Vector3d b, Vector3d sunPosition, double sunRadius, double frequencyMHz)
        {
            Vector3d ab = b - a;
            double length2 = Vector3d.Dot(ab, ab);
            double t = length2 > 1.0 ? Math.Max(0.0, Math.Min(1.0, Vector3d.Dot(sunPosition - a, ab) / length2)) : 0.0;
            double rho = Vector3d.Distance(a + ab * t, sunPosition) / Math.Max(sunRadius, 1.0);   // в радиусах Солнца
            double reach = CORONA_REACH_RADII * Math.Sqrt(CORONA_REFERENCE_MHZ / Math.Max(frequencyMHz, 1.0));
            if (rho >= reach) return 0.0;
            if (rho <= 1.0) return BLOCKED_LOSS_DB;
            double x = (reach - rho) / (reach - 1.0);
            return BLOCKED_LOSS_DB * x * x;
        }

        // --- Шум тел и атмосферы ---

        // Шум тел в луче приёмника, К: Солнце (по частоте), планеты (по температуре), кроме тела, на котором стоит наземная
        // антенна, - его тепло в её собственном шуме. names - кто шумит заметно
        public static double BodiesNoiseK(LinkEnd rx, Vector3d boresight, double gainDbi, double beamDeg, double f, out string names)
        {
            names = "";
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null || boresight.sqrMagnitude < 1e-12) return 0.0;
            double total = 0.0;
            foreach (CelestialBody body in bodies)
            {
                if (body == rx.body && !double.IsNaN(rx.groundAltitude)) continue;
                Vector3d toBody = body.position - rx.position;
                double distance = toBody.magnitude;
                if (distance <= body.Radius) continue;
                double radiusDeg = Math.Asin(body.Radius / distance) * 180.0 / Math.PI;
                double coverage = RadioMath.BodyCoverage(Vector3d.Angle(boresight, toBody), radiusDeg, beamDeg, gainDbi);
                if (coverage < 1e-7) continue;
                double k = BrightnessK(body, f) * coverage;
                total += k;
                if (k >= 1.0) names += (names.Length > 0 ? ", " : "") + $"{body.bodyName} {k:0} К";
            }
            return total;
        }

        // Яркостная температура тела на частоте, К: Солнце - по формуле DSN, тело с атмосферой - её температура у поверхности,
        // без атмосферы - равновесная от Солнца (по альбедо)
        public static double BrightnessK(CelestialBody body, double f)
        {
            if (body.isStar) return RadioMath.SunBrightnessK(f);
            if (body.atmosphere && body.atmosphereTemperatureSeaLevel > 0) return body.atmosphereTemperatureSeaLevel;
            return EquilibriumK(body);
        }

        private static readonly Dictionary<CelestialBody, double> equilibrium = new Dictionary<CelestialBody, double>();

        private static double EquilibriumK(CelestialBody body)
        {
            double k;
            if (equilibrium.TryGetValue(body, out k)) return k;
            k = 0.0;
            CelestialBody sun = Sun;
            if (sun != null && body != sun)
            {
                // Светимость: из физики игры, а если её нет - по потоку у родной планеты
                double luminosity = PhysicsGlobals.SolarLuminosity;
                CelestialBody home = FlightGlobals.GetHomeBody();
                if (luminosity <= 0 && home != null && home.orbit != null)
                    luminosity = home.orbit.semiMajorAxis * home.orbit.semiMajorAxis * 4.0 * Math.PI * PhysicsGlobals.SolarLuminosityAtHome;
                double d = OrbitDistanceToSun(body, sun);
                if (luminosity > 0 && d > 0)
                {
                    double flux = luminosity / (4.0 * Math.PI * d * d);
                    k = Math.Pow(flux * (1.0 - body.albedo) / (4.0 * PhysicsGlobals.StefanBoltzmanConstant), 0.25);
                }
            }
            equilibrium[body] = k;
            return k;
        }

        // Расстояние тела до Солнца: у спутника - его планеты (по большой полуоси, чтобы не мерцало)
        private static double OrbitDistanceToSun(CelestialBody body, CelestialBody sun)
        {
            CelestialBody b = body;
            while (b != null && b.orbit != null && b.orbit.referenceBody != sun) b = b.orbit.referenceBody;
            return b != null && b.orbit != null ? b.orbit.semiMajorAxis : 0.0;
        }

        // Затухание в атмосфере у конца линии, лежащего у поверхности тела с атмосферой, в сторону toward, дБ
        public static double AtmosphereLossDb(LinkEnd end, Vector3d toward, double f)
        {
            if (end.body == null || double.IsNaN(end.groundAltitude) || !end.body.atmosphere) return 0.0;
            double elevation = Elevation(end.body, end.position, toward);
            return RadioMath.AtmosphereZenithLossDb(f, end.body.atmospherePressureSeaLevel / EARTH_PRESSURE_KPA) * RadioMath.AirMass(elevation);
        }

        // Шум атмосферы над наземной антенной, смотрящей вдоль boresight, К
        public static double AtmosphereNoiseK(LinkEnd end, Vector3d boresight, double f)
        {
            if (end.body == null || double.IsNaN(end.groundAltitude) || !end.body.atmosphere) return 0.0;
            return RadioMath.AtmosphereNoiseK(AtmosphereLossDb(end, end.position + boresight, f));
        }

        // Шум приёмника в целом (для дополнения): свой тракт + небо + тела в луче + атмосфера
        public static double ReceiverNoiseK(LinkEnd end, Vector3d boresight, double gainDbi, double beamDeg, double f, double ownK)
        {
            string names;
            return ownK + RadioMath.SkyNoise(f) + BodiesNoiseK(end, boresight, gainDbi, beamDeg, f, out names) + AtmosphereNoiseK(end, boresight, f);
        }

        private static CelestialBody star;

        public static CelestialBody Sun { get { return Star(); } }

        private static CelestialBody Star()
        {
            if (star != null) return star;
            if (FlightGlobals.Bodies == null) return null;
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body.isStar) return star = body;
            }
            return null;
        }

        // --- Связь аппарата целиком ---

        // Прямая линия до каждой станции тел этой игры
        public static void AllStations(LinkRadio tx, List<HopBudget> into)
        {
            DsnManager.LoadStations();
            foreach (DsnStation st in DsnManager.Stations)
            {
                CelestialBody body = DsnManager.BodyOf(st);
                if (body != null) into.Add(ToStation(tx, st, body));
            }
        }

        // Лучший участок: быстрее, при равной скорости - с большим C/N0
        public static HopBudget Best(List<HopBudget> hops)
        {
            HopBudget best = null;
            foreach (HopBudget h in hops)
            {
                if (best == null || h.rateKbps > best.rateKbps || (h.rateKbps == best.rateKbps && h.Cn0 > best.Cn0)) best = h;
            }
            return best;
        }

        // Команды на радио аппарата: лучшая станция с передатчиком в его диапазоне
        public static HopBudget BestUplink(LinkRadio rx)
        {
            DsnManager.LoadStations();
            var hops = new List<HopBudget>();
            foreach (DsnStation st in DsnManager.Stations)
            {
                CelestialBody body = DsnManager.BodyOf(st);
                if (body != null) hops.Add(FromStation(st, body, rx));
            }
            return Best(hops);
        }

        // Команды для участка «радио -> станция приёма»: как в DSN, данные может принимать одна антенна, а команды
        // посылать другая (трёхсторонний режим). Своя станция, если она может командовать этим радио, иначе лучшая из сети
        public static HopBudget CommandFor(DsnStation receiving, CelestialBody body, LinkRadio rx)
        {
            HopBudget own = FromStation(receiving, body, rx);
            if (own.Usable) return own;
            HopBudget best = BestUplink(rx);
            return best != null && best.Usable ? best : own;
        }

        // Лучшая линия вниз с аппарата до Земли среди его радио
        public static HopBudget BestGround(List<LinkRadio> radios, out LinkRadio via)
        {
            via = null;
            HopBudget best = null;
            foreach (LinkRadio r in radios)
            {
                var all = new List<HopBudget>();
                AllStations(r, all);
                HopBudget h = Best(all);
                if (h != null && (best == null || h.rateKbps > best.rateKbps || (h.rateKbps == best.rateKbps && h.Cn0 > best.Cn0)))
                {
                    best = h;
                    via = r;
                }
            }
            return best;
        }

        // Лучший участок между аппаратами: передатчики одного на ретрансляторы (или все приёмники) другого
        public static HopBudget BestBetween(List<LinkRadio> from, List<LinkRadio> to, bool relaysOnly, out LinkRadio rxUsed)
        {
            rxUsed = null;
            HopBudget best = null;
            foreach (LinkRadio tx in from)
            {
                foreach (LinkRadio rx in to)
                {
                    if (relaysOnly && !rx.Relay) continue;
                    HopBudget h = Between(tx, rx);
                    if (best == null || h.rateKbps > best.rateKbps || (h.rateKbps == best.rateKbps && h.Cn0 > best.Cn0))
                    {
                        best = h;
                        rxUsed = rx;
                    }
                }
            }
            return best;
        }

        // Связь линии DSN детали - по пути CommNet (его линии решает этот же расчёт, в обе стороны): каждый участок по
        // антеннам аппаратов, последний - до той станции, на которой путь кончается; команды - тем же путём обратно.
        // Пути нет - лучшая попытка напрямую и почему она не вышла
        public static LinkReport Report(ModuleRadioLink module)
        {
            var report = new LinkReport();
            LinkRadio radio = RadioOf(module);
            AllStations(radio, report.stations);
            // Прямая видимость: антенна раскрыта, и хоть одну станцию не закрывает тело
            foreach (HopBudget h in report.stations)
            {
                if (radio.deployed && !h.blocked.StartsWith("закрывает")) report.visible = true;
            }

            Vessel v = radio.vessel;
            CommPath path = v != null && v.connection != null && v.connection.IsConnected ? v.connection.ControlPath : null;
            if (path != null && path.Count > 0)
            {
                CommNode end = path[path.Count - 1].b;
                if (end != null && end.isHome && !StationHome.ByNode.ContainsKey(end))
                {
                    // Путь до штатной станции KSP: антенны мода сложены, связь держит антенна без мода (встроенная)
                    report.connected = true;
                    report.stock = true;
                    report.why = "связь через антенну без мода (встроенную) до штатной станции - антенны мода сложены";
                    return report;
                }
                if (PathHops(radio, path, report.down, report.up))
                {
                    report.connected = true;
                    report.viaRelay = report.down.Count > 1;
                    report.visible = true;
                    return report;
                }
                report.down.Clear();
                report.up.Clear();
            }

            // Пути нет: лучшая станция напрямую - чего ей не хватило
            HopBudget direct = Best(report.stations);
            if (direct == null)
            {
                report.why = "станций нет";
                return report;
            }
            report.down.Add(direct);
            HopBudget command = BestUplink(radio);
            if (command != null) report.up.Add(command);
            if (direct.blocked.Length > 0) report.why = direct.blocked;
            else if (!direct.Usable) report.why = "сигнал слабее самой медленной линии (10 бит/с)";
            else if (command == null || !command.Usable) report.why = "команды не доходят: " + (command != null && command.blocked.Length > 0 ? command.blocked : "борт не слышит станцию");
            else report.why = "этой антенной связи нет - CommNet ведёт аппарат другим путём";
            return report;
        }

        // Участки пути CommNet по антеннам: вниз от радио аппарата до станции, вверх - обратно. false - путь не по нашим узлам
        private static bool PathHops(LinkRadio radio, CommPath path, List<HopBudget> down, List<HopBudget> up)
        {
            var current = new List<LinkRadio> { radio };
            for (int i = 0; i < path.Count; i++)
            {
                CommNode next = path[i].b;
                StationHome home;
                if (next != null && StationHome.ByNode.TryGetValue(next, out home))
                {
                    if (home.Body == null) return false;
                    HopBudget best = null;
                    LinkRadio via = null;
                    foreach (LinkRadio r in current)
                    {
                        HopBudget h = ToStation(r, home.Station, home.Body);
                        if (best == null || h.rateKbps > best.rateKbps || (h.rateKbps == best.rateKbps && h.Cn0 > best.Cn0))
                        {
                            best = h;
                            via = r;
                        }
                    }
                    if (best == null) return false;
                    down.Add(best);
                    up.Insert(0, CommandFor(home.Station, home.Body, via));
                    return true;
                }
                Vessel nextVessel = VesselOf(next);
                if (nextVessel == null) return false;
                List<LinkRadio> nextRadios = RadiosOf(nextVessel);
                nextRadios.RemoveAll(r => !r.deployed);
                LinkRadio rxUsed;
                HopBudget hop = BestBetween(current, nextRadios, false, out rxUsed);
                if (hop == null) return false;
                down.Add(hop);
                // Обратно (команды): ретранслятор -> предыдущий аппарат
                LinkRadio back;
                HopBudget reverse = BestBetween(new List<LinkRadio> { rxUsed }, current, false, out back);
                if (reverse != null) up.Insert(0, reverse);
                current = nextRadios;
            }
            return false;
        }

        private static readonly Dictionary<CommNode, Vessel> nodeVessels = new Dictionary<CommNode, Vessel>();
        private static int nodeFrame = -1;

        public static Vessel VesselOf(CommNode node)
        {
            if (node == null || node.isHome) return null;
            if (nodeFrame != Time.frameCount)
            {
                nodeFrame = Time.frameCount;
                nodeVessels.Clear();
                if (FlightGlobals.Vessels != null)
                {
                    foreach (Vessel v in FlightGlobals.Vessels)
                    {
                        if (v != null && v.connection != null && v.connection.Comm != null) nodeVessels[v.connection.Comm] = v;
                    }
                }
            }
            Vessel found;
            return nodeVessels.TryGetValue(node, out found) ? found : null;
        }

        // --- Скорости участков для выбора пути CommNet (ядро без дополнения) ---

        public static readonly ILinkRates Rates = new CoreRates();

        private class CoreRates : ILinkRates
        {
            private readonly Dictionary<uint, Vessel> vessels = new Dictionary<uint, Vessel>();
            private readonly Dictionary<uint, double> home = new Dictionary<uint, double>();
            private readonly Dictionary<ulong, double> pairs = new Dictionary<ulong, double>();
            private float refreshed = float.NegativeInfinity;

            // CommNet спрашивает скорости тысячи раз: считаем раз в секунду - за секунду аппараты сдвигаются мало
            private void Refresh()
            {
                if (Time.realtimeSinceStartup - refreshed < RATES_REFRESH_S && vessels.Count > 0) return;
                refreshed = Time.realtimeSinceStartup;
                vessels.Clear();
                home.Clear();
                pairs.Clear();
                if (FlightGlobals.Vessels == null) return;
                foreach (Vessel v in FlightGlobals.Vessels)
                {
                    if (v != null) vessels[v.persistentId] = v;
                }
            }

            private Vessel Find(uint id)
            {
                Refresh();
                Vessel v;
                return vessels.TryGetValue(id, out v) ? v : null;
            }

            public bool Knows(uint vessel)
            {
                return RadiosOf(Find(vessel)).Count > 0;
            }

            public double HomeRate(uint from, double distanceM)
            {
                Refresh();
                double rate;
                if (home.TryGetValue(from, out rate)) return rate;
                LinkRadio via;
                HopBudget h = BestGround(RadiosOf(Find(from)), out via);
                rate = h != null && h.Usable ? h.rateKbps * 1000.0 : 0.0;
                home[from] = rate;
                return rate;
            }

            public double VesselRate(uint from, uint to, double distanceM)
            {
                Refresh();
                ulong key = ((ulong)from << 32) | to;
                double rate;
                if (pairs.TryGetValue(key, out rate)) return rate;
                LinkRadio rx;
                HopBudget h = BestBetween(RadiosOf(Find(from)), RadiosOf(Find(to)), true, out rx);
                rate = h != null && h.Usable ? h.rateKbps * 1000.0 : 0.0;
                pairs[key] = rate;
                return rate;
            }

            public double HopRate(uint from, uint to, bool toHome, double distanceM, out double cn0, out string mode)
            {
                cn0 = double.NegativeInfinity;
                mode = "";
                LinkRadio used;
                HopBudget h = toHome ? BestGround(RadiosOf(Find(from)), out used) : BestBetween(RadiosOf(Find(from)), RadiosOf(Find(to)), true, out used);
                if (h == null || !h.Usable) return 0.0;
                cn0 = h.Cn0;
                mode = h.mode;
                return h.rateKbps * 1000.0;
            }
        }

        // Скорости участка: дополнение знает про радиоблоки аппарата - его числа главнее; иначе - расчёт ядра
        public static ILinkRates RatesFor(uint vessel)
        {
            ILinkRates plus = RadioHooks.Rates;
            return plus != null && plus.Knows(vessel) ? plus : Rates;
        }
    }
}
