# Contraseñas Argon2id

`PasswordHasher.Hash` genera Argon2id v19 con 19 MiB, 2 iteraciones,
paralelismo 1, hash de 32 bytes y sal aleatoria gestionada por
Isopoh.Cryptography.Argon2 2.0.0. La cadena PHC completa se guarda en
`Users.PasswordHash` (TEXT); no requiere migración del esquema.

Las altas, cambios de contraseña y nuevas cuentas iniciales ya utilizan ese
método. Las contraseñas SHA-256/Base64 existentes se comprueban temporalmente
en tiempo constante y se sustituyen al iniciar sesión correctamente. La
actualización condicionada al hash anterior evita sobrescribir un cambio
concurrente de contraseña; en ese caso se rechaza el acceso y se debe reintentar.
Las sesiones ya abiertas no desencadenan la migración hasta un nuevo login.

La verificación rechaza formatos inválidos, variantes distintas de Argon2id,
versiones distintas de v19 y costes fuera de los límites admitidos: memoria
8–65536 KiB, iteraciones 1–10 y paralelismo 1–4. Estos límites permiten leer
otros perfiles existentes sin aceptar costes arbitrariamente grandes; el
monitor siempre genera el perfil indicado arriba. Si se cambia la política,
hay que revisar los límites y añadir la renovación de perfiles Argon2 antiguos.

## Verificación

```powershell
dotnet run --project tests/PasswordChecks/PasswordChecks.csproj --configuration Release
```

Las pruebas usan SQLite en memoria y no abren `aps.db`. Cubren sal aleatoria,
Unicode y espacios, acceso correcto/incorrecto, datos malformados, costes
excesivos, persistencia de la migración y cambios de contraseña concurrentes.

## Despliegue

- Hacer una copia consistente de SQLite antes de desplegar. Una versión antigua
  del monitor no podrá validar las contraseñas ya migradas.
- Reiniciar con la nueva compilación y medir el coste con la concurrencia real,
  especialmente en ARM32. Limitar intentos y concurrencia de login es una tarea
  adicional; los límites del hash no sustituyen esos controles.
- Fijar un plazo para restablecer las contraseñas de cuentas inactivas y retirar
  posteriormente la verificación SHA-256.
- Este cambio no sustituye las credenciales predeterminadas de `Program.cs`:
  hay que cambiarlas antes de usar el monitor en producción.
