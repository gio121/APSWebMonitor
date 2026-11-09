# Visibilidad del sinóptico FLP

Configurada el 05/11/2026 en las ventanas 27 y 30 del borrador activo
`FLP/.Working/a04e20b021ae451eb1d54729573d27de`, basado en la revisión 0.5.
Referencia: `C:\REPO\Monitores\QT8594_MONFLP\MON\frmSynoptic.cs` y
`frmSynoptic.resx`. Se modificaron únicamente propiedades de visibilidad de
78 elementos por ventana; la geometría, lecturas y colores se conservaron.

| Elementos | Condición |
| --- | --- |
| DAC0–DAC21, etiquetas y valores | Ocultos, como los 44 controles de los recursos originales. La función DACVisibility del original tiene las asignaciones comentadas. |
| T Control | `en_timers1_dc`, bit 8 |
| DC Short | `en_timers1_dc`, bit 4 |
| DC OvC | `en_timers1_dc`, bit 13 |
| BAT OvC | `en_timers1_dc`, bit 7 |
| Discharge | `en_timers2_in`, bit 3 |
| Low ON | `en_timers2_in`, bit 4 |
| I Fan | `en_timers1_ac`, bit 11, y modo administrador del visor |
| Reintentos KHV | `status_aux_5`, bit 6; si falta ese dato, `khv_retrials.COUNT > 0` |
| High ON DC / AC | `i2tpcm_fan_dc` / `i2tpcm_fan`, mayor que 0 y menor o igual que 100 |
| Barras Overload3PH / OverloadN y sus valores | `ovlpcm_ac` / `ovlpcm_n_ac` distinto de 0 |

Las mismas reglas se aplican al título, barra y valor de cada temporizador.
Los títulos de sobrecarga conservan su condición adicional por código de fallo.
La llave física de mantenimiento que exige el original para I Fan se representa
mediante el modo administrador existente en el visor web; no se integra un dongle.

El editor permite cambiar «Ocultar elemento en el visor», «Solo en modo
administrador», las comparaciones y la señal alternativa. Los elementos ocultos
siguen seleccionables y atenuados en el editor. La señal alternativa se usa solo
cuando falta el valor principal: un cero válido no se sustituye.

Preparación y verificación: `.tools/flp-visibility/Tool.csproj` y
`tests/SinopticoRenderingChecks`. Copia de seguridad:
`.tools/backups/20261105-154539-flp-visibility/aps-before.db`.
La escritura comprueba el borrador activo y el contenido original de cada ventana,
usa una transacción y verifica el resultado. No se publicó ninguna revisión.
