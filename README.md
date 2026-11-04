# RealisticRadio

*[Русская версия](README.ru.md)*

Realistic radio communications for Kerbal Space Program 1.12, replacing the stock CommNet model. Every link is decided
by a radio link budget: transmitter power and antenna, path loss, receiving antenna and system noise temperature, the
resulting C/N0, and the modulation and coding mode it supports. Only ModuleManager is required.

The interface is available in English and Russian and follows the game's language.

## Compatibility

**Required:** Kerbal Space Program 1.12.x, ModuleManager 4.2.x.

**Planet packs**

| Pack | Support |
|---|---|
| Stock system | yes: Kerbol scale (k = 10.6), Kerbin station network |
| Sol (Real / Quarter / Stock) | yes: scale from `SystemScale` (1 / 4 / 9), Earth stations at their real locations |
| Real Solar System | yes: k = 1, Earth stations |
| Other packs (KSRSS, JNSQ, OPM, GPP...) | work, but Kerbol scale is used; Kerbin stations, or on the home planet if there is no Kerbin |

**Antennas from other mods** switch to this calculation automatically (`AntennasAuto.cfg`), for any mod using the stock
`ModuleDataTransmitter`. Checked: Bluedog Design Bureau, Tantares, RN Soviet Probes and Rockets, Tundra Exploration,
Starship Expansion Project, HabTech2, JX2 Antenna, ReStock+, CommNet Antennas Extension, IQ Blue Moon.

**Incompatible:** mods that replace the CommNet network or science transmission themselves:
- RealAntennas
- RemoteTech (the plugin; RemoteTech parts without the plugin work)
- CommNet Constellation

## How it works

Full details are in the [wiki](https://github.com/Falcon-9FT/RealisticRadio/wiki).

**Link budget.** For each hop the mod computes the power at the receiver input:
`P_rx = P_tx − L_match + G_tx − L_point − L_fs − L_diff − L_atm − L_corona + G_rx − L_feed − L_point`,
where `L_fs = 20·lg d[km] + 20·lg f[MHz] + 32.44` is the free-space path loss.
Carrier-to-noise density: `C/N0 = P_rx − 10·lg(k·T_sys)`.

**Antennas.** Parabolic gain `G = η·(πD/λ)²`. The radiation pattern is the ITU-R F.699 reference pattern (main lobe,
first sidelobe, sidelobe envelope `32 − 25·lg θ`, back level), which gives the pointing loss. Feed-line matching follows
a resonant band (VSWR 2 at the edges): outside it, reflection losses grow, and a dish feed passes nothing below its
waveguide cutoff. Input filters at stations and relays reject other bands.

**Signal path.**
- Obstructions: every body on the path is a sphere, and loss beyond its edge is computed as diffraction per ITU-R P.526
  (Fresnel-Kirchhoff parameter ν, with a correction for a rounded obstacle), using the terrain height under the ends of
  the link.
- Atmosphere: clear-sky zenith attenuation by frequency (rising towards the 22 GHz water vapour line), multiplied by the
  Kasten-Young airmass and by the ratio of surface pressure to Earth's.
- Solar corona: extra attenuation by the closest approach ρ of the beam to the centre of the Sun. The affected zone is
  `ρ_max = 12·√(8420 / f[MHz])` R☉ (wider at low frequencies); inside it the loss grows quadratically with
  `(ρ_max − ρ) / (ρ_max − 1)`, up to 60 dB at the photosphere.

**Receiver noise.** `T_sys = T_rx + T_sky + T_bodies + T_atm`:
- sky: galactic background `60·λ^2.55` plus the 2.7 K cosmic microwave background;
- bodies in the beam: the Sun with brightness temperature `5672·λ[mm]^0.24517` K (DSN 810-005, module 105), planets by
  atmospheric or equilibrium temperature; the share collected follows the body's solid angle (omnidirectional antenna) or
  its area in the beam, attenuated by the F.699 pattern;
- atmosphere above a ground antenna: `T_atm = 275·(1 − 10^(−L_atm/10))` K.

**Data rate.** The modem picks the fastest mode whose `Es/N0` threshold holds in the channel bandwidth (with a 1 dB
implementation margin): from BPSK with a rate 1/6 turbo code (−7 dB) to 32APSK 9/10 (16 dB), following the DVB-S2 and
CCSDS tables. When the signal cannot fill the whole band, the symbol rate drops at the most robust mode, down to
10 bit/s; below that (C/N0 ≈ 12 dB-Hz) there is no link.

**Ground stations.** About 50 stations (NASA DSN, ESA, Russian stations, NASA Near Space Network and others) in
`DsnStations.cfg`: coordinates, diameter, noise temperature, receive bands and transmitter power per band. A station
receives only its own frequencies. Commands may be sent by another station of the network (three-way mode) if the
receiving one cannot transmit in the spacecraft's band.

**CommNet.** This calculation decides each link between a vessel and a station, or between two vessels: the link exists
only if both data down and commands up get through. The route is chosen by the highest bottleneck data rate, not by the
stock "signal strength". A vessel with no deployed antennas of this mod connects under the stock rules.

**System scale.** Antenna and transmitter parameters are real, so `L_fs` uses the distance `d·k`, where `k` is how many
times smaller the system is than the real one: Sol by `SystemScale` (Real 1, Quarter 4, Stock 9), Real Solar System 1,
Kerbol system 10.6. Geometry, Doppler shift and signal delay use in-game distances. An unknown planet pack gets a warning
and the Kerbol `k`.

**Science transmission.** Data leaves through a queue at the bottleneck rate of the route, in game time, including during
time warp and for vessels in the background. Transmitter power draw is `P = P_rf / 0.35 + 5 W` (1 unit of electric
charge = 1 kJ). Science is credited per whole file or in tenths of a file.

**Difficulty.** On Hard, a vessel with no link loses manual control (SAS and throttle remain). Easy: science transmission
at ×10 speed with no electric charge used. Normal: after a dropout, transmission resumes where it stopped. Moderate and
Hard: a file sent whole is sent again from the start after a dropout.

## Copyright

Copyright (c) 2026 Falcon 9.

Licensed under the GNU General Public License v3.0. See [LICENSE.txt](LICENSE.txt).
