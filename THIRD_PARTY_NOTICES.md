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
