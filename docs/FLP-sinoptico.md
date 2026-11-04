# Sinóptico de ejemplo FLP

Configurado el 03/11/2026 en la ventana 27 del borrador de FLP, basado en la revisión 0.1. Se conservan los 237 elementos y su geometría; 111 elementos tienen lecturas o reglas dinámicas.

Fuentes: `C:\REPO\Monitores\QT8594_MONFLP\MON\frmSynoptic.cs` y `xmlconfig.cmfx` (versión 67, 29/10/2026). Las 82 señales utilizadas corresponden al nodo 2, trama de monitor FT 18, longitud 504 bytes. Se mantienen los identificadores de las señales existentes y se actualizan sus metadatos desde FLP.

Las reglas de color, texto, visibilidad, mando/retorno invertido y cuenta atrás se guardan en el propio elemento y son editables desde el editor. Los símbolos pasivos conservan su función gráfica. Los valores sin telemetría muestran `--`; las condiciones invertidas no se activan con datos ausentes.

## Limitaciones de las fuentes

- `iinvn_fil`: la lectura está comentada en el monitor y no existe en el CMFX. El componente muestra `IinvN: N/D`, conservando el indicador de fallo 431.
- DAC10, `dc_ovc_first_lvl_m`: existe una referencia de trama pero falta su definición de tipo y escala. Muestra `N/D`.
- `vps`: la trama conserva este nombre, pero su definición está como `ilvpspri`. Se verificó la coincidencia de descripción, tipo UINT, escala 0.050611477, offset 0 y unidad V con `vps` en `xmlconfig_old7.cmfx`. Se usa la posición 128 de la trama actual.

## Temporizadores

T Control, DC Short, DC OvC, BAT OvC, I Fan y Low ON representan `(máximo - contador) / máximo`. Reintentos KHV y Discharge representan `contador / máximo`. Los indicadores junto a las barras muestran el valor físico del contador; High ON y Overload usan porcentajes.

## Señales configuradas

| Tag | ID | Byte | Tipo | Escala | Offset | Unidad |
| --- | ---: | ---: | --- | ---: | ---: | --- |
| `status1_i` | 3232 | 28 | UINT16 | 1 | 0 |  |
| `status_aux_1` | 3233 | 30 | UINT16 | 1 | 0 |  |
| `status_aux_2` | 3234 | 32 | UINT16 | 1 | 0 |  |
| `status_aux_3` | 3235 | 34 | UINT16 | 1 | 0 |  |
| `status_aux_4` | 3236 | 36 | UINT16 | 1 | 0 |  |
| `regul_ac` | 3241 | 46 | UINT16 | 1 | 0 |  |
| `regul_dc` | 3242 | 48 | UINT16 | 1 | 0 |  |
| `fpga_clkn1_dout_i` | 3250 | 64 | UINT16 | 1 | 0 |  |
| `fpga_clkn1_din_i` | 3252 | 68 | UINT16 | 1 | 0 |  |
| `fpga_clkn2_din_i` | 3253 | 70 | UINT16 | 1 | 0 |  |
| `fpga_din_mux_1` | 3254 | 72 | UINT16 | 1 | 0 |  |
| `cfails_1_ac` | 3787 | 76 | UINT16 | 1 | 0 |  |
| `status_aux_5` | 3256 | 78 | UINT16 | 1 | 0 |  |
| `cfails_1_dc` | 3789 | 82 | UINT16 | 1 | 0 |  |
| `cfails_1_in` | 3788 | 86 | UINT16 | 1 | 0 |  |
| `vac_st_fil` | 3761 | 126 | UINT16 | 0,254509831501831 | 0 | V |
| `vps` | 3747 | 128 | UINT16 | 0,050611477 | 0 | V |
| `vhv2` | 3749 | 136 | UINT16 | 1 | 0 | V |
| `vhv2_chp1` | 3751 | 138 | UINT16 | 1 | 0 | V |
| `vhv2_chp2` | 3753 | 140 | UINT16 | 1 | 0 | V |
| `ichp1_fil` | 3750 | 146 | UINT16 | 0,125080357997558 | 0 | A |
| `ichp2_fil` | 3752 | 148 | UINT16 | 0,125080357997558 | 0 | A |
| `vchp2_1` | 3755 | 152 | UINT16 | 0,250522951753358 | 0 | V |
| `vchp2_2` | 3756 | 154 | UINT16 | 0,250522951753358 | 0 | V |
| `vchp_fil` | 3754 | 156 | UINT16 | 0,250522951753358 | 0 | V |
| `iinv_fil` | 3294 | 158 | UINT16 | 0,25079382979243 | 0 | A |
| `vac_r_fil` | 3298 | 160 | UINT16 | 0,146941319728989 | 0 | V |
| `vac_s_fil` | 3299 | 162 | UINT16 | 0,146941319728989 | 0 | V |
| `vac_t_fil` | 3300 | 164 | UINT16 | 0,146941319728989 | 0 | V |
| `iac_r_fil` | 3301 | 166 | UINT16 | 0,302290995916288 | 0 | A |
| `iac_s_fil` | 3302 | 168 | UINT16 | 0,302290995916288 | 0 | A |
| `iac_t_fil` | 3303 | 170 | UINT16 | 0,302290995916288 | 0 | A |
| `iac_n_fil` | 3304 | 172 | UINT16 | 0,0345964225875946 | 0 | A |
| `tem_cntl_fil` | 3305 | 174 | UINT16 | 0,4296875 | -273 | ºC |
| `vdc` | 3758 | 194 | UINT16 | 0,0506581196581197 | 0 | V |
| `idc_fil` | 3316 | 196 | UINT16 | 0,0627658034188034 | 0 | A |
| `ibat` | 3759 | 198 | INT16 | 0,0487311398974359 | 0 | A |
| `exttb_fil` | 3318 | 200 | UINT16 | 0,0522588522588523 | -86 | ºC |
| `ilvps` | 3757 | 202 | UINT16 | 0,0312576312576313 | 0 | A |
| `ovlpcm_n_ac` | 3320 | 204 | UINT8 | 1 | 0 | % |
| `ovlpcm_ac` | 3324 | 208 | UINT8 | 1 | 0 | % |
| `fanspeed_mode` | 3760 | 211 | UINT8 | 1 | 0 |  |
| `DT_PASDISCH_IN` | 3341 | 240 | UINT16 | 1 | 0 | s |
| `pasdisch_in.COUNT` | 3342 | 242 | UINT16 | 1 | 0 | s |
| `DT_IFAN_AC` | 3782 | 268 | UINT16 | 0,001 | 0 | s |
| `ifan_ac.COUNT` | 3781 | 270 | UINT16 | 0,001 | 0 | s |
| `DT_COVT_DC` | 3357 | 272 | UINT16 | 1 | 0 | s |
| `covt_dc.COUNT` | 3358 | 274 | UINT16 | 1 | 0 | s |
| `DT_SHT_DC` | 3361 | 280 | UINT16 | 1 | 0 | s |
| `sht_dc.COUNT` | 3362 | 282 | UINT16 | 1 | 0 | s |
| `DT_OVCBAT_DC` | 3780 | 284 | UINT16 | 0,001 | 0 | s |
| `ovcbat_dc.COUNT` | 3779 | 286 | UINT16 | 0,001 | 0 | s |
| `DT_OVCOUT_DC` | 3365 | 288 | UINT16 | 1 | 0 | s |
| `ovcout_dc.COUNT` | 3366 | 290 | UINT16 | 1 | 0 | s |
| `DT_KHV_RETRIALS` | 3778 | 292 | UINT16 | 1 | 0 | s |
| `khv_retrials.COUNT` | 3777 | 294 | UINT16 | 1 | 0 | s |
| `hv_input_overvoltage_m` | 3762 | 376 | UINT16 | 0,488250257552011 | 19,800745069893 | V |
| `hv_input_undervoltage_m` | 3763 | 378 | UINT16 | 0,24758554575777 | 0 | V |
| `chp_ovc_first_lvl_m` | 3764 | 380 | UINT16 | 0,363988837 | 47,9388586110841 | A |
| `chp_ovc_fail_lvl_m` | 3765 | 382 | UINT16 | 0,586689195531775 | 77,269431146156 | A |
| `chp_overvoltage_m` | 3766 | 384 | UINT16 | 0,220615650032982 | 29,0560076950739 | V |
| `inv_ovc_first_lvl_m` | 3767 | 386 | UINT16 | 0,454995486 | 59,9248165458199 | A |
| `inv_ovc_fail_lvl_m` | 3768 | 388 | UINT16 | 0,469512223750745 | 59,91017752 | A |
| `vac_short_m` | 3769 | 390 | UINT16 | 0,604302634759488 | 79,5891950688905 | A |
| `neu_short_m` | 3770 | 392 | UINT16 | 0,0715067180561614 | 9,41773509621229 | A |
| `dc_overvoltage_m` | 3771 | 394 | UINT16 | 0,091250867 | 12,018122422163 | V |
| `dc_ovc_fail_lvl_m` | 3772 | 398 | UINT16 | 0,144555024 | 19,0385038208061 | A |
| `inv_heat_ptc_m` | 3773 | 400 | UINT16 | 1 | 0 | c |
| `chp_heat_ptc_m` | 3774 | 402 | UINT16 | 1 | 0 | c |
| `lvps_heat_ptc_m` | 3424 | 404 | UINT16 | 1 | 0 | c |
| `lvps_priovc_first_lvl_m` | 3775 | 406 | UINT16 | 0,050430752 | 6,640319255 | A |
| `dc_inovc_fail_lvl_m` | 3776 | 408 | UINT16 | 0,097211233 | 12,8031277131048 | A |
| `vac_presence_m` | 3427 | 410 | UINT16 | 0,119560361257495 | 28,17447947 | V |
| `blower_low_m` | 3428 | 412 | UINT16 | 0,3744667 | 0 | mArms |
| `blower_high_m` | 3429 | 414 | UINT16 | 0,3744667 | 0 | mArms |
| `vhv_presence_high` | 3430 | 416 | UINT16 | 1,04162218071937 | 42,2424667279835 | V |
| `vhv_presence_low` | 3431 | 418 | UINT16 | 0,268770386233796 | 0 | V |
| `i2tpcm_fan` | 3784 | 442 | UINT8 | 1 | 0 | % |
| `i2tpcm_fan_dc` | 3783 | 443 | UINT8 | 1 | 0 | % |
| `DT_FANSPEED_H2L_IN` | 3786 | 444 | UINT16 | 1 | 0 | s |
| `fanspeed_h2l_in.COUNT` | 3785 | 446 | UINT16 | 1 | 0 | s |
| `car_number` | 3748 | 448 | UINT8 | 1 | 0 |  |

## Verificación y recuperación

Compilación de la aplicación y pruebas `tests/SinopticoRenderingChecks` correctas. Se comprueban bits altos, igualdad de códigos de fallo, inversión del retorno DC, discrepancias de contactores, temporizadores, ausencia de telemetría y conservación de las reglas al serializar. No se ha validado contra un equipo FLP físico.

Copia de seguridad local: `.tools/backups/20261103-125749-bindings/aps-before-bindings.db`. En la misma carpeta, `binding-report.json` contiene las reglas completas y los metadatos aplicados. La revisión original y el contenido de las otras ventanas se verificaron sin cambios.

La aplicación debe reiniciarse con esta compilación para activar el nuevo renderizado y recargar los metadatos. No se ha publicado una nueva revisión.
