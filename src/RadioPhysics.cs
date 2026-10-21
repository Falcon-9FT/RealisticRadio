// Copyright (c) 2026 Falcon 9.
// This work is licensed under the GNU General Public License v3.0.
// To view a copy of this license, visit https://www.gnu.org/licenses/gpl-3.0.html

namespace RealisticRadio
{
    public static class RadioPhysics
    {
        // Связь линии DSN детали с Землёй - единым расчётом ядра (LinkCalc): напрямую до станции или через ретрансляторы,
        // что быстрее и что разрешает маршрут аппарата; команды - тем же путём обратно. Видимость, горизонт и тела на
        // пути - LinkCalc.Obstruction
        public static LinkResult CalculateBestLink(ModuleRadioLink module)
        {
            return LinkResult.From(LinkCalc.Report(module));
        }
    }
}
