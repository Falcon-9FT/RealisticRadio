// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

using System;

// Ядро мода: дополнения ставят зависимость от него (KSPAssemblyDependency), чтобы KSP загружала их после ядра
[assembly: KSPAssembly("RealisticRadio", 1, 0)]

namespace RealisticRadio
{
    // Как отдавать данные аппарата: быстрее как получится, только напрямую на Землю или только через ретранслятор
    public enum DataRoute { Auto, Direct, Relay }

    // Скорости участков связи по передатчикам и антеннам аппаратов, бит/с (0 - неизвестна)
    public interface ILinkRates
    {
        bool Knows(uint vessel);
        double HomeRate(uint from, double distanceM);
        double VesselRate(uint from, uint to, double distanceM);
        double HopRate(uint from, uint to, bool toHome, double distanceM, out double cn0, out string mode);
    }

    // Деталь с радиоаппаратурой дополнения (радиоблок, ответчик): для вкладок редактора
    public interface IRadioUnitPart { }

    // Точки подключения ядра. Ядро считает связь само; дополнение (RealisticRadioPlus) при загрузке подставляет сюда
    // то, что знает только оно: маршрут, заданный аппарату в окне мода, каналы ретрансляции радиоблоков и скорости
    // участков по своей сети данных
    public static class RadioHooks
    {
        // Маршрут данных аппарата; без дополнения - выбор сам, по скорости
        public static Func<uint, DataRoute> RouteOf = id => DataRoute.Auto;

        // Аппарат - ретранслятор по своей аппаратуре (канал «Ретранслятор» радиоблока)
        public static Func<Vessel, bool> IsRelay = v => false;

        // Скорости участков; null - неизвестны, CommNet выбирает путь по силе сигнала
        public static ILinkRates Rates;

        // Дополнение установлено (радиоблоки, SDR): подсказки ядра говорят о нём, только если оно есть
        public static bool PlusInstalled;
    }
}
