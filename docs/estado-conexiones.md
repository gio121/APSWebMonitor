# Indicadores de comunicaciones y control

Todos los diseños de pantalla incluyen una franja inferior de 32 px con dos
indicadores alineados a la derecha. Se reserva espacio en el contenido y el menú
lateral; el reproductor de sesiones, también minimizado, queda por encima.

Verde significa que el monitor recibió una respuesta SEPSA válida de ese nodo
en los últimos tres segundos. Rojo significa que no hay respuesta reciente
confirmada. Comunicaciones corresponde al origen 3 y control al origen 2.
Ambos estados se calculan independientemente de las señales cargadas en el CMFX.

Abrir un socket UDP, enviar un comando sin respuesta o reproducir una sesión
no confirma conectividad. Se valida dirección y puerto del emisor UDP, cabecera,
longitud, destino, origen y checksum de la trama. Se acepta el checksum SEPSA
aditivo y la variante +2 del cliente existente. Cambiar equipo/puerto o desconectar
borra las confirmaciones. Si se detiene la recepción, los indicadores caducan;
no se envían consultas adicionales ni se interfiere con la reprogramación.

La vista comprueba el estado cada 500 ms y solo se vuelve a dibujar cuando cambia.
Los colores se acompañan de texto accesible y una descripción al pasar el cursor.

Verificación: `dotnet run --project tests/ConnectionHealthChecks`.
