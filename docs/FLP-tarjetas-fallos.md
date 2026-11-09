# Tarjetas de fallos FLP

Referencia: `C:\REPO\Monitores\QT8594_MONFLP\MON\frmSynoptic.cs` y `xmlconfig.cmfx`.

| Tarjeta | Last stop | Failures | Failures count |
|---|---|---|---|
| Inversor / Inverter | laststop_ac (3247) | cfails_1_ac (3787) | nfails_ac (3321) |
| LVPS | laststop_dc (3248) | cfails_1_dc (3789) | nfails_dc (3322) |
| Chopper | laststop_in (3249) | cfails_1_in (3788) | nfails_in (3323) |

Los ID corresponden al borrador FLP de la revisión 0.7 activo el 6 de noviembre de 2026. Se guardaron en `FailuresConfigJson` de las ventanas 27 y 30; los campos se pueden editar en la configuración de fallos.

`laststop_*` se decodifica como máscara de bits de la propia señal. Ya no se infiere de los fallos observados durante la reproducción. `cfails_1_*` es un código enumerado, decodificado con las definiciones CMFX importadas del nodo correspondiente. Cuando vuelve a cero, el visor conserva el último fallo mostrado en gris sobre amarillo suave, como la referencia. Sin datos muestra un guion y borra esa retención.

Las descripciones están vinculadas a `stop_*`, `status_aux_*` y `regul_*` del nodo 2, en ese orden, como `txtInfoInverter`, `txtInfoLVPS` y `txtInfoChopper` de la referencia. No usan `laststop_*`.

| Tarjeta | Máscara stop | Máscara status_aux | Máscara regul |
|---|---|---|---|
| Inversor | FFFF | FFEF | 100F |
| LVPS | FFFF | FFF7 | 18FF |
| Chopper | FFFF | FFFF | 0007 |

Las máscaras reproducen las operaciones sobre los bytes de `frmSynoptic.cs`, incluidos los bits 11 y 12 de `regul_dc`. Solo se muestran bits con descripción definida, como en la referencia. La zona de 56 px tiene scroll vertical independiente. Doble clic o Enter abre el diálogo completo con las mismas señales y filtros.

Verificación: compilación y `tests/ConverterInfoChecks`, que incluye pruebas de las asignaciones, máscara de última parada, código enumerado, cero, datos ausentes/no finitos y serialización.

Copia de seguridad previa a la asignación: `.tools/backups/2026-11-06T09-37-39-377Z-failure-cards/aps-before.db`.
