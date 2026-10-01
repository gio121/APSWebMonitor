# Comandos reales de control

En **Comandos → Nuevo comando / Editar**, seleccionar:

- **6A**: modificación de variable. Requiere subcomando hexadecimal, variable de
  control del nodo 2 y valor a escribir. El valor es **bruto**, sin aplicar escala
  ni offset del CMFX. Se codifica con el tipo de la variable asociada; la variable
  sirve para validar y codificar el dato, el subcomando identifica la operación
  en el equipo. No se envía el ID de la base de datos ni su posición en la trama
  de monitorización.
- **7A**: comando interno, con subcomando hexadecimal (0000–FFFF).

El diálogo valida variable disponible, tipo, rango y valores enteros cuando
corresponde. Soporta UINT8, INT8, UINT16, INT16, UINT32, INT32, FLOAT32 y BCD_BYTE.
Los campos técnicos quedan guardados en `Commands`; el identificador `CommandValue`
se conserva al editar para no alterar las selecciones ya guardadas en ventanas.
Cancelar una edición no modifica el objeto original.

Solo se permite enviar si la conexión y la monitorización están activas y no hay
reprogramación. La condición se comprueba también en el servicio justo antes del
envío, después de esperar al transporte. Se envía un único datagrama por pulsación,
entre consultas de monitorización, sin detener el bucle y sin reintentos automáticos.

Destino SEPSA 2, origen 1. Subcomando little endian. Para 6A se añaden los bytes del
valor inmediatamente después del subcomando, según `GetModVarsFrame` de la biblioteca
de referencia MetroMadrid. Para 7A se conserva el payload estándar del monitor:
subcomando y seis bytes cero. El checksum es SEPSA aditivo.

El resultado **ENVIADO** significa que se transmitió el datagrama, no que el equipo
haya confirmado su ejecución. El log guarda tipo, subcomando, variable/valor y trama.

Los comandos antiguos sin tipo/subcomando configurados siguen visibles, pero no se
ejecutan ni generan resultados OK simulados. Hay que editarlos para configurar 6A
o 7A. Se ha retirado la creación automática de 8A en esta pestaña: ese proceso
detiene la monitorización y pertenece a la funcionalidad de reprogramación.

`CommandSchema.EnsureControlCommands` añade las columnas anulables a bases existentes
al arrancar, sin asignar operaciones ejecutables a los comandos antiguos.

Pruebas: `dotnet run --project tests/ControlCommandChecks`. Se comprueban tramas
conocidas, codificación, límites, migración, persistencia y envíos con un simulador
UDP local. No se han enviado comandos a equipos físicos.
