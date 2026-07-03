# TaskbarHero Trainer 1.8.5 - instalacion y uso

Esta release incluye el trainer externo, el bridge BepInEx IL2CPP y el payload
necesario para instalarlo en TaskbarHero.

## Instalacion

1. Cierra TaskbarHero.
2. Extrae `TaskbarHeroTrainer-v1.8.5.zip` en una carpeta cualquiera.
3. Abre PowerShell en esa carpeta.
4. Ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File .\install_taskbarhero_trainer.ps1
```

El instalador:

- detecta la carpeta de Steam de TaskbarHero;
- instala o actualiza BepInEx IL2CPP;
- copia `TaskbarHeroModMenu.dll` a `BepInEx\plugins`;
- aplica el parche persistente de heroes en `GameAssembly.dll` si coincide con
  la version esperada;
- si Steam actualizo `GameAssembly.dll`, mueve interop/cache antiguos para que
  BepInEx los regenere en el siguiente arranque;
- deja Doorstop en `enabled = false` para que abrir el juego normal desde Steam
  no cargue el trainer;
- abre `TaskbarHeroTrainer.exe` al terminar.

Si Steam esta en otra ruta:

```powershell
powershell -ExecutionPolicy Bypass -File .\install_taskbarhero_trainer.ps1 -GameDir "D:\SteamLibrary\steamapps\common\TaskbarHero"
```

## Abrir con trainer

1. Abre `TaskbarHeroTrainer.exe`.
2. Pulsa `Launch game`.
3. Espera a que el juego cargue.
4. Pulsa `Refresh runtime` si el bridge aun no aparece como conectado.

`Launch game` activa el bridge solo para ese arranque. Si abres TaskbarHero
directamente desde Steam, el juego queda normal.

## Botones

- `+999.999.999 currency`: suma `999.999.999` a las monedas actuales.
- `Unlock + level heroes`: desbloquea heroes y aplica nivel alto persistente.
- `Unlock inventory/stash`: desbloquea inventario, alijo y paginas del alijo.
- El Trade Ship no se desbloquea desde el trainer porque el upload a Steam/backend valida esos slots.
- `Unlock all pets`: desbloquea mascotas.
- `Skill pts 0`: deja los puntos libres en `0`.
- `Skill pts 999`: deja los puntos libres en `999`.
- `Add item`: crea un item por `ItemKey`.
- `List useful item keys`: genera una lista de claves utiles.
- `Repair equip dupes`: repara referencias duplicadas del equipo equipado.
- `Best <class> gear`: crea el mejor equipo detectado para esa clase.
- `<class> gems + socket`: aplica gemas al equipo equipado de esa clase.
- `One hit kill`: activa/desactiva kill rapido.
- `God mode`: activa/desactiva invulnerabilidad para los heroes.
- `Game speed`: cambia la velocidad del juego y se reaplica si el cambio de
  stage intenta devolverla a `1.0x`.

Clases:

- Knight
- Ranger
- Sorcerer
- Priest
- Hunter
- Assassin

## Flujo recomendado de equipo y gemas

1. Pulsa `Best <class> gear`.
2. Equipa manualmente las piezas en el personaje.
3. Pulsa `<class> gems + socket`.

El socket solo actua sobre equipo equipado. No toca el stash.

## Actualizar

1. Cierra el juego.
2. Extrae la nueva release en una carpeta limpia.
3. Ejecuta otra vez `install_taskbarhero_trainer.ps1`.

El instalador crea backups de archivos sustituidos dentro de la carpeta del
juego.

## Desinstalar bridge

Para abrir el juego siempre limpio:

1. Borra `BepInEx\plugins\TaskbarHeroModMenu.dll`.
2. Abre `doorstop_config.ini`.
3. Deja `enabled = false`.

El trainer no toca tu save al estar cerrado.
