// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;
using System.Collections.Generic;
using CommNet;
using UnityEngine;

namespace RealisticRadio
{
    // Связь в CommNet решает расчёт мода (LinkCalc): линия между аппаратом с антеннами мода и станцией из DsnStations.cfg
    // (или другим таким аппаратом) есть, только если участок работает в обе стороны. Так у игры одна правда о связи:
    // управление, наука, карта и окно «Линия связи» видят одно и то же.
    // CommNet выбирает путь по скорости линий, а не по «силе сигнала». В стоке маршрут - с наибольшим произведением сил
    // сигнала участков, а сила почти 1, пока до предела дальности далеко: прямая линия с Марса на Землю «сильная», хотя
    // идёт со скоростью сотен бит в секунду, и марсоход шлёт напрямую, даже когда над ним орбитальный ретранслятор с
    // мегабитами. Здесь путь - с наибольшей скоростью слабейшего участка (при ретрансляции всё идёт со скоростью узкого
    // места), скорости участков - по передатчикам и антеннам аппаратов, как у сети данных. Маршрут, заданный аппарату
    // («Только напрямую», «Через ретранслятор» - окно мода, вкладка «Данные»), сдвигает выбор, но связь не обрывает
    public class RadioCommNetwork : CommNetwork
    {
        private const double PREFERENCE = 0.01;      // участок, нежелательный по маршруту аппарата, - в оценке как в сто раз хуже
        private const double HOP_PENALTY = 0.9999;   // при равной скорости - путь короче

        private readonly Dictionary<CommNode, Vessel> vessels = new Dictionary<CommNode, Vessel>();

        public override void Rebuild()
        {
            vessels.Clear();
            if (FlightGlobals.Vessels != null)
            {
                foreach (Vessel v in FlightGlobals.Vessels)
                {
                    if (v != null && v.connection != null && v.connection.Comm != null) vessels[v.connection.Comm] = v;
                }
            }
            base.Rebuild();
        }

        // Радиоблок с каналом «Ретранслятор» делает аппарат ретранслятором и для CommNet: его антенны ретранслируют, даже
        // если по конфигу они «прямой связи» (силу ретрансляции игра пересчитывает перед каждой сборкой сети - ставим после)
        protected override void PreUpdateNodes()
        {
            base.PreUpdateNodes();
            foreach (KeyValuePair<CommNode, Vessel> pair in vessels)
            {
                CommNode node = pair.Key;
                if (node.antennaTransmit == null || node.antennaRelay == null || node.antennaRelay.power >= node.antennaTransmit.power) continue;
                if (!RadioHooks.IsRelay(pair.Value)) continue;
                node.antennaRelay.Update(node.antennaTransmit.power, node.antennaTransmit.rangeCurve, node.antennaTransmit.combined);
            }
        }

        // --- Есть ли линия между двумя узлами: решает расчёт мода ---

        // Как решается пара узлов: стоковым CommNet (аппарат без антенн мода, штатные станции), нет линии или есть
        private enum Verdict { Stock, None, Link }

        private struct Judgement
        {
            public Verdict verdict;
            public double forward, reverse;   // бит/с: a -> b и b -> a
        }

        private readonly Dictionary<long, Judgement> judged = new Dictionary<long, Judgement>();
        private float judgedAt = float.NegativeInfinity;
        private const float JUDGE_REFRESH_S = 1f;     // за секунду аппараты сдвигаются мало, а пар узлов - тысячи

        // Аппарат с антеннами мода (хоть одна раскрыта) связывается только с нашими станциями и такими же аппаратами,
        // и только если участок работает в обе стороны: данные вниз и команды вверх. Аппарат без антенн мода или со всеми
        // сложенными (ракета на старте) живёт по стоковым правилам со штатными станциями - как раньше
        protected override bool SetNodeConnection(CommNode a, CommNode b)
        {
            Judgement j = Judge(a, b);
            if (j.verdict == Verdict.Stock) return base.SetNodeConnection(a, b);
            if (j.verdict == Verdict.None)
            {
                Disconnect(a, b, true);
                return false;
            }
            double distance = Vector3d.Distance(a.precisePosition, b.precisePosition);
            CommLink link = Connect(a, b, distance);
            if (link == null) return false;
            double strength = Quality(Math.Min(j.forward, j.reverse));
            link.Set(a, b, distance, strength);
            link.aCanRelay = a.isHome || (a.antennaRelay != null && a.antennaRelay.power > 0);
            link.bCanRelay = b.isHome || (b.antennaRelay != null && b.antennaRelay.power > 0);
            link.bothRelay = link.aCanRelay && link.bCanRelay;
            link.strengthAR = link.strengthBR = link.strengthRR = strength;
            link.SetSignalStrength(strength);
            return true;
        }

        private Judgement Judge(CommNode a, CommNode b)
        {
            if (Time.realtimeSinceStartup - judgedAt > JUDGE_REFRESH_S)
            {
                judged.Clear();
                judgedAt = Time.realtimeSinceStartup;
            }
            long key = ((long)a.GetHashCode() << 32) ^ (uint)b.GetHashCode();
            Judgement j;
            if (judged.TryGetValue(key, out j)) return j;
            j = Compute(a, b);
            judged[key] = j;
            return j;
        }

        private Judgement Compute(CommNode a, CommNode b)
        {
            var stock = new Judgement { verdict = Verdict.Stock };
            var none = new Judgement { verdict = Verdict.None };
            StationHome homeA, homeB;
            bool isHomeA = StationHome.ByNode.TryGetValue(a, out homeA), isHomeB = StationHome.ByNode.TryGetValue(b, out homeB);
            if (isHomeA && isHomeB) return none;
            List<LinkRadio> radiosA = isHomeA ? null : Deployed(a), radiosB = isHomeB ? null : Deployed(b);
            bool modA = radiosA != null && radiosA.Count > 0, modB = radiosB != null && radiosB.Count > 0;

            // Наша станция и аппарат с антеннами мода - расчётом; со стоковым аппаратом наша станция не связывается
            if (isHomeA || isHomeB)
            {
                StationHome home = isHomeA ? homeA : homeB;
                List<LinkRadio> radios = isHomeA ? radiosB : radiosA;
                if (radios == null || radios.Count == 0 || home.Body == null) return none;
                // Данные вниз - на эту станцию; команды - с неё же или, если она не может (нет передатчика в диапазоне
                // борта, не хватает мощности), с другой станции сети: так DSN и работает в трёхстороннем режиме
                double down = 0, up = 0;
                foreach (LinkRadio r in radios)
                {
                    HopBudget toStation = LinkCalc.ToStation(r, home.Station, home.Body);
                    if (!toStation.Usable) continue;
                    down = Math.Max(down, toStation.rateKbps * 1000.0);
                    HopBudget fromStation = LinkCalc.FromStation(home.Station, home.Body, r);
                    up = Math.Max(up, fromStation.Usable ? fromStation.rateKbps * 1000.0 : AnyUplink(r));
                }
                if (down <= 0 || up <= 0) return none;
                return new Judgement { verdict = Verdict.Link, forward = isHomeA ? up : down, reverse = isHomeA ? down : up };
            }

            // Штатная станция KSP: аппарат с антеннами мода с ней не связывается (его «дом» - наши станции)
            if (a.isHome || b.isHome) return (a.isHome ? modB : modA) ? none : stock;

            // Два аппарата: оба с антеннами мода - расчётом в обе стороны, иначе стоковым CommNet
            if (!modA || !modB) return stock;
            LinkRadio used;
            HopBudget ab = LinkCalc.BestBetween(radiosA, radiosB, false, out used);
            HopBudget ba = LinkCalc.BestBetween(radiosB, radiosA, false, out used);
            if (ab == null || ba == null || !ab.Usable || !ba.Usable) return none;
            return new Judgement { verdict = Verdict.Link, forward = ab.rateKbps * 1000.0, reverse = ba.rateKbps * 1000.0 };
        }

        // Лучшие команды на радио с любой станции сети, бит/с (0 - ни одна не достаёт). Раз в обновление оценок на радио:
        // иначе каждая пара «станция - аппарат» перебирала бы все станции заново
        private readonly Dictionary<string, double> anyUplink = new Dictionary<string, double>();
        private float anyUplinkAt = float.NegativeInfinity;

        private double AnyUplink(LinkRadio radio)
        {
            if (anyUplinkAt != judgedAt)
            {
                anyUplink.Clear();
                anyUplinkAt = judgedAt;
            }
            string key = (radio.vessel != null ? radio.vessel.persistentId : 0u) + "/" + radio.name + "/" + radio.FrequencyMHz;
            double rate;
            if (anyUplink.TryGetValue(key, out rate)) return rate;
            HopBudget best = LinkCalc.BestUplink(radio);
            rate = best != null && best.Usable ? best.rateKbps * 1000.0 : 0.0;
            anyUplink[key] = rate;
            return rate;
        }

        // Раскрытые линии DSN аппарата этого узла (null - узел не аппарат)
        private List<LinkRadio> Deployed(CommNode node)
        {
            Vessel v;
            if (!vessels.TryGetValue(node, out v) || v == null) return null;
            List<LinkRadio> radios = LinkCalc.RadiosOf(v);
            radios.RemoveAll(r => !r.deployed);
            return radios;
        }

        // Скорость данных аппарата до Земли по его пути CommNet, бит/с: узкое место участков, которые решал мод
        // (участки стокового CommNet скорость не ограничивают). 0 - пути нет. Работает и для аппаратов в фоне
        public static double DownlinkRate(Vessel v)
        {
            RadioCommNetwork net = CommNetNetwork.Instance != null ? CommNetNetwork.Instance.CommNet as RadioCommNetwork : null;
            if (net == null || v == null || v.connection == null || !v.connection.IsConnected) return 0.0;
            CommPath path = v.connection.ControlPath;
            if (path == null || path.Count == 0) return 0.0;
            double rate = double.PositiveInfinity;
            CommNode current = v.connection.Comm;
            for (int i = 0; i < path.Count; i++)
            {
                CommLink link = path[i];
                CommNode from = link.start, to = link.end;
                if (current != null && to == current)
                {
                    from = link.end;
                    to = link.start;
                }
                double r = net.JudgedRate(from, to);
                if (r >= 0) rate = Math.Min(rate, r);
                current = to;
            }
            return double.IsPositiveInfinity(rate) ? 0.0 : rate;
        }

        // Скорость a -> b, если линию решал мод, бит/с; -1 - решал стоковый CommNet
        private double JudgedRate(CommNode a, CommNode b)
        {
            Judgement j = Judge(a, b);
            if (j.verdict == Verdict.Stock) return -1;
            return j.verdict == Verdict.Link ? j.forward : 0;
        }

        protected override void UpdateShortestPath(CommNode a, CommNode b, CommLink link, double bestCost, CommNode startNode, CommNode endNode)
        {
            double cost = Cost(a, b, link, bestCost, startNode);
            if (cost <= 0 || cost <= b.bestCost) return;
            Improve(a, b, link, cost);
        }

        protected override CommNode UpdateShortestWhere(CommNode a, CommNode b, CommLink link, double bestCost, CommNode startNode, Func<CommNode, CommNode, bool> whereClause)
        {
            double cost = Cost(a, b, link, bestCost, startNode);
            if (cost <= 0 || cost <= b.bestCost) return null;
            Improve(a, b, link, cost);
            return whereClause(startNode, b) ? b : null;
        }

        // Как в стоке: лучший путь до b - через a, b ещё раз в очередь на обход
        private void Improve(CommNode a, CommNode b, CommLink link, double cost)
        {
            b.bestCost = cost;
            b.bestLinkNode = a;
            b.bestLink = link;
            if (b.isInCandidateList || b.pathingID == pathingID) return;
            candidates.Enqueue(b);
            b.isInCandidateList = true;
            b.pathingID = pathingID;
        }

        // Оценка пути до b через a: скорость узкого места (0..1 по логарифму бит/с) с поправкой на маршрут аппарата
        private double Cost(CommNode a, CommNode b, CommLink link, double bestCost, CommNode start)
        {
            double signal = link.GetSignalStrength(start);
            if (signal <= 0) return 0;
            // Скорость участка: решал мод - по его расчёту, иначе по сети данных дополнения или стоковой силе сигнала
            double rate = JudgedRate(a, b);
            if (rate < 0) rate = Rate(a, b);
            // Участка нет по расчёту (за горизонтом, другой диапазон, слабее 10 бит/с) - путь через него не идёт
            if (rate == 0) return 0;
            // Скорость не знаем (антенны не мода) - по стоковой силе сигнала, но как очень медленную линию
            double quality = rate > 0 ? Quality(rate) : 0.05 * signal;
            Vessel origin;
            if (a == start && vessels.TryGetValue(start, out origin))
            {
                DataRoute route = RadioHooks.RouteOf(origin.persistentId);
                if ((route == DataRoute.Relay && b.isHome) || (route == DataRoute.Direct && !b.isHome)) quality *= PREFERENCE;
            }
            return Math.Min(bestCost, quality) * HOP_PENALTY;
        }

        private static double Quality(double bitsPerSecond)
        {
            return Math.Max(1e-6, Math.Min(1.0, Math.Log10(1.0 + bitsPerSecond) / 10.0));
        }

        // Скорость участка a -> b по передатчикам и антеннам аппаратов, бит/с; 0 - участка нет, -1 - неизвестна
        private double Rate(CommNode a, CommNode b)
        {
            Vessel va, vb;
            bool hasA = vessels.TryGetValue(a, out va);
            bool hasB = vessels.TryGetValue(b, out vb);
            // Дополнение знает радиоблоки аппарата - его скорости главнее; иначе считает ядро
            ILinkRates net = LinkCalc.RatesFor(hasA ? va.persistentId : hasB ? vb.persistentId : 0u);
            hasA = hasA && net.Knows(va.persistentId);
            hasB = hasB && net.Knows(vb.persistentId);
            double distance = Vector3d.Distance(a.precisePosition, b.precisePosition);
            if (hasA && b.isHome) return net.HomeRate(va.persistentId, distance);
            if (hasB && a.isHome) return net.HomeRate(vb.persistentId, distance);
            if (hasA && hasB) return net.VesselRate(va.persistentId, vb.persistentId, distance);
            return -1;
        }

        // Подменить сеть CommNet нашей: раз за сцену и снова, если игра пересоздала сеть (смена настроек). Чужую сеть
        // (другого мода) не трогаем
        public static void Install()
        {
            CommNetNetwork network = CommNetNetwork.Instance;
            if (network == null || network.CommNet == null || network.CommNet.GetType() != typeof(CommNetwork)) return;
            network.CommNet = new RadioCommNetwork();
            // Узлы, станции и тела сами добавятся в новую сеть по этому событию
            GameEvents.CommNet.OnNetworkInitialized.Fire();
            Debug.Log("[RealisticRadio] CommNet: путь связи выбирается по скорости линий (через ретранслятор, если так быстрее)");
        }
    }
}
