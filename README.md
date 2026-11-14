# FTX1-CATandRemote · Etapa 1

Portare în C# / .NET 8 și Avalonia a nucleului CAT din [Eugen68/ftx1-pult](https://github.com/Eugen68/ftx1-pult).
Sursa de adevăr utilizată: commit `d453771d8ee1f8d9784dca6ef3db9754c08cdb63` (versiunea 2.10.2).
Planul complet se află în [PLAN.md](PLAN.md). Această etapă nu include audio, waterfall, decodoare sau acces de pe telefon.

## Pornire din surse

Instalează .NET SDK 8 (versiunile pachetelor sunt fixate în proiecte).

```sh
dotnet restore Ftx1.sln
dotnet build Ftx1.sln -c Release
dotnet test Ftx1.sln -c Release
dotnet run --project src/Ftx1.App
```

La prima pornire este selectat **Simulator**. Apasă **Conectare** pentru a încerca VFO-A/B, benzile,
modurile, sliderele, contoarele și consola CAT. Rotița mouse-ului pe o cifră schimbă cifra respectivă.
Introducerea frecvenței respectă aplicația originală: `7.074` = MHz, `14200` = kHz, `145500000` = Hz.

Pentru stație: debifează Simulator, alege portul **Enhanced** pentru CAT, viteza identică meniului
stației (implicit 38400) și metoda PTT. RTS/DTR necesită portul **Standard**, separat de CAT.
Configurarea se salvează atomic în `config.json` lângă aplicație, cu rezervă în
`%APPDATA%/FTX1-CATandRemote/` când directorul aplicației nu poate fi scris.

## Protecția emisiei

PTT funcționează cât timp ții apăsat butonul sau Space. Eliberarea, pierderea focusului, Esc,
deconectarea și închiderea ferestrei opresc emisia. O singură sursă poate deține emisia simultan.
Watchdog-ul limitează emisia la 180 s implicit (configurabil 10–600 s). Nucleul include și limita de
1,5 s pentru un viitor client remote; Etapa 1 nu deschide niciun server remote.

Oprirea trimite `KY00;TX0;` și coboară RTS/DTR inclusiv când scrierea CAT eșuează.
Consola și secvențele CAT nu permit ocolirea protecției prin comenzi brute care pornesc emisia;
acestea trebuie executate prin operațiile protejate ale nucleului.

**De verificat pe stație:** controlul în ambele sensuri, calibrarea contoarelor, Enhanced/Standard COM,
PTT prin TX1/TX2/RTS/DTR și oprirea la închiderea ferestrei în timpul emisiei. Prima verificare se face
cu sarcină artificială. Testele simulatorului nu confirmă funcționarea hardware.

## Organizare

- `src/Ftx1.Core` — protocolul CAT, porturile seriale, coada de comenzi, Rig, configurația și protecția PTT.
- `src/Ftx1.Sim` — simulator CAT, inclusiv EX, memorii și temporizările tunerului/CW; fără sinteză audio.
- `src/Ftx1.App` — interfață Avalonia MVVM; textele în `Resources/Strings.ro.axaml`.
- `tests/Ftx1.Core.Tests` — 680 teste xUnit, inclusiv comparații cu vectorii generați de Python.
- `tests/Ftx1.App.Tests` — 2 teste Avalonia headless pentru controale, sincronizare și închiderea cu PTT activ.
- `tests/data` — vectori pentru toate cele 64 de reguli CAT, 414 cazuri de comenzi și o secvență de simulator.
- `tools/generate_reference.py` — regenerarea vectorilor și a tabelelor/parserelor din sursa de adevăr.

Proiectele Audio, Dsp și Ft8, biblioteca nativă și clientul Android se introduc în etapele dedicate,
nu sunt implementate ca funcții fictive în Etapa 1.

Pentru regenerarea referințelor, cu dependențele Python ale proiectului original instalate:

```sh
python tools/generate_reference.py ../ftx1-pult
```

CI compilează și rulează testele pe Linux și Windows. Executabilul Windows se publică **numai manual**:
Actions → Stage 1 validation → Run workflow → `publish_windows`. Nu se generează executabile la push
sau merge, respectând regula de a face build-uri de distribuție doar la cerere.

## Proveniență

Vezi [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Acest repo nu acordă o licență nouă asupra
codului original. Condiția privind acordul autorului înaintea distribuirii publice, menționată în PLAN.md,
rămâne în responsabilitatea proprietarului proiectului.
