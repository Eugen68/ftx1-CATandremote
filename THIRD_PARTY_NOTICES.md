# Proveniență și drepturi

Etapa 1 este o portare derivată din `Eugen68/ftx1-pult`, fork al `Evgeny161rus/ftx1-pult`.
Autorul original este identificat în proiect ca R6AAF. Au fost consultate și portate comportamentele din:

- `server/cat.py`: tabele, toate regulile de răspuns, formate, interogări, polling, limite și acțiuni.
- `server/sim.py`: simulatorul CAT, starea inițială, comenzile de memorii și temporizările.
- `server/main.py`: proprietatea emisiei, watchdog și oprirea sigură.
- `server/cw.py`: tabelul Morse utilizat doar pentru durata simulată a memoriei CW.
- `web/app.js`: benzi, moduri, introducerea frecvențelor și calibrarea contoarelor.

Referința exactă este păstrată în `tests/data/source.json`.
Proprietarul a declarat în PLAN.md v2 că aspectul legal este rezolvat. Atribuirea surselor se păstrează;
această portare nu acordă o licență nouă asupra codului derivat. Pictograma serverului provine din
`ftx1-pult/web/icon-192.png`.

Dependențele NuGet au propriile licențe; sunt folosite versiunile declarate în fișierele `.csproj`.
`ft8_lib` nu este inclus în Etapa 1. Când va fi introdus, trebuie păstrată licența sa MIT.

## Etapa 3 — audio

- `ftx1-pult/server/audio.py` (`guess_codec`) și `server/sim.py` (`SimAudio`), plus
  `web/worklets.js`: referințe pentru selecția dispozitivelor, buffering și simulator.
  Simulatorul audio C# este simplificat (zgomot, tonuri și ecou), fără decodoarele digitale Python.
- Concentus 2.2.2: implementare Opus C# de Logan Stromberg și contribuitori, BSD-3-Clause;
  include portări ale codului Opus/SILK/CELT/Speex. Vezi notificările upstream:
  https://github.com/lostromb/concentus/blob/master/LICENSE
- NAudio 2.2.1: Mark Heath și contribuitori, MIT; include resampler-ul WDL și notificările aferente:
  https://github.com/naudio/NAudio/blob/v2.2.1/license.txt

Pachetele NuGet și sursele upstream păstrează textele complete ale licențelor componentelor lor.
