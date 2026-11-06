# Verificación de Wanqix 1.0

Fecha: 6 de noviembre de 2026. Anfitrión: Windows 11 (10.0.26100), SDK .NET 10.0.400 / MSBuild 18.9.6. Código F# con `LangVersion=10.0`, proyectos `net10.0`, advertencias tratadas como errores.

## Comandos reproducibles

```powershell
cd A:\Wanqix\1.0
.\tools\verify.ps1 -Publish
dotnet build wanqix/wanqix.fsproj -c Release
```

`tools/verify.ps1` compila Debug y Release, ejecuta la suite, comprueba un ejemplo CLI con datos temporales, verifica lectura persistente en otra ejecución y comprueba que guest reciba `WQX-SEC-001` con salida 1 al detener logger. Con `-Publish` genera `publish/win-x64/Wanqix.exe` (framework-dependent, runtime .NET 10 requerido).

Resultado final: **Debug y Release sin errores ni advertencias; 364 aserciones aprobadas; CLI, persistencia, rechazo esperado y publicación win-x64 aprobados**. El proyecto original compatible también compila en Release.

Actualización de diagnóstico de ciclo de vida: `TestResults/lifecycle-verification.log` recoge la nueva comprobación. Las pruebas adicionales verifican el orden real de inicio de servicios, proceso activo informado durante reinicio, servicios detenidos en el snapshot, éxito final posterior al guardado, tiempos monotónicos, ausencia de contraseñas en mensajes, ausencia de secuencia tras denegación y `[FAILED]` sin falso éxito cuando falla el almacenamiento. Se comprobó por CLI una secuencia completa de inicio/reinicio con un proceso activo y una región asignada.

El log de la comprobación final queda en `TestResults/verification.log` (ignorado por Git). Las pruebas CLI conservan su directorio temporal y muestran su ubicación; jamás operan sobre datos personales. Las contraseñas de los fixtures no se usan en la instalación normal.

## Cobertura de la suite

| Grupo | Evidencia comprobada |
|---|---|
| Kernel | Arranque Running, apagado Stopped, llamadas tras cierre rechazadas, fallo de persistencia conduce a Failed |
| Anillos | Proceso/región Ring 0 protegidos incluso ante root; servicio Ring 1 limitado; root Ring 2; usuario Ring 3 |
| WSA | Tokens desconocidos/revocados/expirados rechazados, roles consultados en cada operación, revocación inmediata, reautenticación para subir a Ring 2 |
| Autenticación | Contraseña incorrecta, cinco fallos y cooldown determinista, recuperación tras avanzar reloj, secretos fuera de snapshots/historial |
| Procesos | PIDs crecientes y únicos, 32 creaciones concurrentes, ejecución real del contador, suspensión/reanudación, terminación y reclamación |
| Memoria | Tamaños/cuotas inválidos, separación entre usuarios, reserva del supervisor, first-fit y rechazo por fragmentación sin alterar mapa |
| Archivos | Propiedad y lectura pública, ancestros privados, rutas de escape/Windows/control rechazadas, movimiento de árbol y borrado no vacío |
| Persistencia | Archivos, usuarios, configuración, historial y boot counter; respaldo válido recuperado, primario/respaldo dañados rechazados, exclusión de instancia |
| Servicios | Dependientes impiden parada, inicio recursivo, disponibilidad efectiva de WVFS/login, contadores de trabajo |
| KSL | Comillas/escapes, equivalencia rápida/estructurada en IR, variables, alias/ciclos, filtros tipados, sort/select/take, redirección y scripts WVFS |
| KSL negativo | Anillos/capacidades declarados rechazados, campos desconocidos, opciones duplicadas, operadores truncados, errores de columnas, cancelación |
| Robustez | 200 entradas generadas con semilla fija; parseo reproducible o error posicionado estable; salida escapa controles de terminal |

Los rechazos se contrastan con el estado protegido antes/después, además de verificar su código de error. Los cambios de auditoría durante un rechazo son intencionales.

## Prueba interactiva realizada

Se ejecutó la consola real a través de una **PTY/ConPTY**, además del modo no interactivo:

- Arranque y prompt guest coherente con la sesión.
- `sys.i` + Tab completó `sys.info`, y Enter ejecutó la consulta.
- Flecha arriba recuperó el comando anterior y permitió volver a ejecutarlo.
- `usr.login root` pidió contraseña sin eco y cambió el prompt a root.
- `sys.reboot` guardó, cerró y abrió otra sesión guest.
- Ctrl+C descartó una línea y Ctrl+D cerró con código 0.
- El `Wanqix.exe` publicado, con reloj automático, completó `auto.worker` con 5 unidades de trabajo sin `sys.tick`.
- La revisión del historial persistido del fixture no encontró el login ni su contraseña.

Esto comprueba entrada/salida y flujo en una terminal real proporcionada por el entorno de ejecución. No se realizó una inspección visual de la interfaz gráfica de Windows Terminal, ni QA manual de redimensionamiento, IME, emojis de doble ancho, accesibilidad o consolas antiguas. No se presenta esa cobertura como verificada.

## Límites de las garantías

Las pruebas demuestran las políticas del modelo y las rutas comprobadas; no equivalen a una auditoría independiente ni a aislamiento del anfitrión. Los componentes .NET compilados, el sistema de archivos anfitrión y sus permisos son de confianza. Los snapshots no están firmados/cifrados, el journal tiene retención acotada y los chequeos de enlaces no resisten un atacante anfitrión que sustituya directorios concurrentemente. No se cargó código externo ni se implementó un sandbox IPC ficticio. El alcance exacto de cada simplificación está en README y security-rings.md.
