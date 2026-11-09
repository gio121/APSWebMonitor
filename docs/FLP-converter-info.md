# Converter Info de FLP

Configurado el 05/11/2026 en el borrador de **FLP, revisión 0.4**, carpeta `.Working/d376fc18d7b145139c50163d868be5bf`. Se aplica a las ventanas **27, sinoptico FLP**, y **30, sinoptico FLP (copia)**.

Referencia: `C:\REPO\Monitores\QT8594_MONFLP\MON\frmSynoptic.cs` y `xmlconfig.cmfx`. Las 35 señales ya existentes coinciden en nodo, posición, tipo, escala, offset y textos con la fuente FLP. Se conservan sus IDs y los textos en inglés del CMFX que ya utiliza el proyecto.

| Campo | Señales en orden de presentación | IDs | Formato |
| --- | --- | --- | --- |
| Control · SW | Nodo 2, `SW_NAME_1` a `SW_NAME_6` | 3206–3211 | ASCII concatenado |
| Control · versión SW | Nodo 2, `SW_VER_H`, `SW_VER_L` | 3213, 3212 | ASCII, separados por punto |
| Control · FPGA | Nodo 2, `FPGA_VER6` a `FPGA_VER1` | 3220–3215 | ASCII en orden inverso |
| Control · versión FPGA | Nodo 2, `FPGA_OFF_VER` | 3222 | BCD decodificado dividido entre 10 |
| Comunicaciones · SW | Nodo 3, `SW_NAME_1` a `SW_NAME_6` | 3490, 3858–3862 | ASCII concatenado |
| Comunicaciones · versión SW | Nodo 3, `SW_VER_L`, `SW_VER_H` | 3863, 3864 | ASCII concatenado, por ejemplo `0A` |
| Comunicaciones · fecha | Nodo 3, `rtim_seconds`, `rtim_minutes`, `rtim_hours`, `rtim_day`, `rtim_month`, `rtim_year` | 3545–3550 | Bytes binarios, año + 2000, sin conversión horaria |

El detalle reproduce el orden de las señales en la trama de monitor del nodo 2:

| Señal | ID | Byte | Máscara efectiva |
| --- | ---: | ---: | --- |
| `status1_i` | 3232 | 28 | `FDFF` |
| `status_aux_2` | 3234 | 32 | `CA10` |
| `status_aux_3` | 3235 | 34 | `1F00` |
| `status_aux_4` | 3236 | 36 | `43C2` |
| `status_aux_aps` | 3237 | 38 | `FF3F` |
| `status_aux_5` | 3256 | 78 | `FFFF` |

Las máscaras incluyen todas las operaciones sobre los bytes del monitor original. En `status_aux_3`, los bits 0 y 1 se borran antes de aplicar la máscara final `03` al byte bajo; por tanto, ese byte queda en cero. `status_aux_1` tiene comentada la incorporación a Converter Info. Las señales `fpga_din_mux_2` y `comms_events` tampoco se añaden a este panel en FLP.

La configuración queda guardada en `Windows.ConverterInfoConfigJson` y puede editarse desde SinopticoEditor. La fecha de comunicaciones usa `UINT8`, mientras que el control usa BCD para su versión FPGA. Las reglas de MetroMadrid son diferentes y no se aplican a esta configuración.

Verificación: 163 comprobaciones sobre máscaras, textos, orden de versiones, fecha binaria y lectura de la configuración guardada. Se conservan los demás campos de las ventanas, las definiciones de señales, los archivos de las revisiones publicadas, el manifiesto del borrador y la selección de proyecto. No se ha publicado otra revisión ni se ha validado con un equipo físico.

Copia de seguridad: `.tools/backups/20261105-090805-flp-converter-info/aps-before.db`, con el manifiesto anterior y el informe de configuración en la misma carpeta. La herramienta local de preparación y validación está en `.tools/flp-converter-info`.
