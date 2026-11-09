# Información del convertidor en el sinóptico

El visor muestra una tarjeta junto a las tarjetas de fallos. Incluye **Unidad de control**, **Comunicaciones** e **Información del convertidor**. Se actualiza con la monitorización activa o con el fotograma de la sesión reproducida.

En **SinopticoEditor → Configurar información del convertidor** se puede ocultar la tarjeta, elegir las señales de cada campo, ordenar los bytes, configurar el formato y añadir señales de detalle con una máscara hexadecimal. **Aplicar** actualiza el formulario; **Guardar** conserva la configuración en la ventana del proyecto. Cancelar el diálogo descarta sus cambios.

La opción **Sustituir señales por las del monitor** rellena los campos para los nodos de control y comunicaciones seleccionados. Las ventanas anteriores usan estos valores automáticamente cuando los nodos se identifican sin ambigüedad. Si hay varios nodos candidatos, deben seleccionarse en el diálogo.

Correspondencia con `QT8816_MONMETROMADRID/MON/frmSynoptic.cs` y su `xmlconfig.cmfx`:

| Campo | Señales y formato |
| --- | --- |
| Control · SW | `SW_NAME_1` a `SW_NAME_6`, ASCII |
| Control · versión SW | `SW_VER_H`, punto, `SW_VER_L`, ASCII |
| Control · FPGA | `FPGA_VER6` a `FPGA_VER1`, ASCII |
| Control · versión FPGA | `FPGA_OFF_VER / 10`, un decimal |
| Comunicaciones · SW | Los primeros seis bytes: `SW_NAME_1` y `SW_NAME_2_U_BOOT` a `SW_NAME_6_U_BOOT`; también admite los nombres sin sufijo |
| Comunicaciones · versión SW | `SW_VER_L_U_BOOT`, `SW_VER_H_U_BOOT`, `SW_VER_I_U_BOOT`, ASCII concatenado |
| Comunicaciones · fecha | `rtim_seconds`, `rtim_minutes`, `rtim_hours`, `rtim_day`, `rtim_month`, `rtim_year`, ya decodificados de BCD; `dd/MM/yyyy HH:mm:ss`, sin desplazamiento horario |

El detalle muestra textos de bits activos de `status1_i`, `status_aux_2` a `status_aux_7`, `status_aux_aps`, `fpga_din_mux_2` y `comms_events`. Las máscaras iniciales reproducen los filtros del monitor: `FDFF`, `CA20`, `FFFF` para los auxiliares 3–7, `FF3F`, `FF20` y `FF08`, respectivamente. Los bits sin texto no generan entradas. Estas señales informativas no incrementan los contadores de fallos ni modifican los valores usados por otras tarjetas.

Los campos incompletos o sin datos muestran `--`. La configuración se guarda en `Windows.ConverterInfoConfigJson`; el inicializador añade la columna al abrir proyectos anteriores, incluidos los borradores recuperados al reiniciar. La recuperación conserva los cambios del borrador y no marca como pendiente un borrador limpio solo por la actualización del esquema. La revisión publicada permanece intacta.

Verificación: `tests/ConverterInfoChecks` comprueba versiones, orden de bytes, máscaras, nodos duplicados, RTC, ausencia de datos, renderizado y actualización/persistencia de proyectos anteriores.
