# Comandos KSL disponibles

`help` enumera la información del registro real; `help nombre` muestra su uso. Los permisos de propietario/anillo/capacidad se vuelven a comprobar al ejecutar, aunque un comando figure en la ayuda.

| Comando | Uso / resultado |
|---|---|
| `sys` / `sys.info` | Versión, estado, arranques y estadísticas de memoria lógica |
| `sys.off` | Apagado con guardado; administración |
| `sys.reboot` | Apaga y solicita nuevo arranque; administración |
| `sys.tick [cantidad]` | Avanza 1–10 000 ticks; administración |
| `sys.config [clave valor]` | Lee configuración o cambia `memory.capacity`, `process.limit`, `tick.ms`, `history`, `color`; cambios administrativos |
| `proc` / `proc.list` | PID, propietario, anillo, estado, prioridad, trabajo realizado, cuota, creación y diagnóstico |
| `proc.spawn nombre [--priority n] [--memory tamaño] [--steps n]` | Crea trabajo finito Ring 3; prioridad 0–9, pasos 1–1 000 000 |
| `proc.end pid` | Finaliza y libera regiones propias/autorizadas |
| `proc.pause pid` / `proc.resume pid` | Suspende/reanuda trabajo |
| `mem` / `mem.map` | Regiones visibles: ID, dirección, tamaño, PID, propietario y anillo |
| `mem.alloc pid tamaño` | Reserva memoria lógica dentro de la cuota del proceso |
| `mem.free región` | Libera región autorizada |
| `fs` / `fs.list [ruta]` | Lista entradas con metadatos; por defecto cwd |
| `fs.go ruta` | Cambia cwd tras validar acceso y tipo directorio |
| `fs.new ruta` | Crea directorio con padre existente y autorizado |
| `fs.write ruta "texto"` | Crea/reemplaza texto virtual |
| `fs.read ruta` | Lee contenido virtual |
| `fs.del ruta` | Elimina archivo/directorio vacío autorizado |
| `fs.move origen destino` | Mueve o renombra; árbol completo sin mezclar con destino existente |
| `fs.permit ruta true\|false` | Lectura pública de entrada propia; ancestros también deben permitir acceso |
| `dev` / `dev.list` | `disk0` (WVFS), `mem0` (regiones), `timer0` (reloj lógico) |
| `dev.info nombre` | Información del dispositivo simulado indicado |
| `usr` / `usr.info` | Usuario, anillo y capacidades actuales |
| `usr.list` | Usuarios, IDs, roles y grupos; administración |
| `usr.new nombre contraseña` | Crea usuario ordinario y home; administración |
| `usr.login nombre contraseña` | Autentica y revoca sesión anterior |
| `usr.logout` | Revoca sesión actual y abre guest |
| `usr.grant nombre rol` / `usr.revoke nombre rol` | Concede/revoca `user` o `admin`; administración; root está protegido |
| `svc` / `svc.list` | Estados, dependencias, anillos, capacidades y contadores |
| `svc.start nombre` / `svc.stop nombre` / `svc.restart nombre` | Administración de servicios con dependencias |
| `log` / `log.show` | Registro técnico; `log.read` |
| `log.audit` | Auditoría de autorizaciones e identidades; `security.audit.read` |
| `set nombre valor` | Define variable de sesión |
| `alias nombre comando` | Define alias de nombre de orden |
| `run ruta.ksl` | Ejecuta script WVFS con permisos actuales |
| `where columna valor` | Filtro de igualdad sobre tabla canalizada |
| `sort columna [--desc]` | Orden tipado |
| `select columna ...` | Proyección de columnas |
| `take cantidad` | Limita filas |
| `help [comando]` | Ayuda contextual del registro |
| `version` | Versión y plataforma |
| `clear` | Solicita limpieza de terminal |

En consola interactiva se puede escribir `usr.login nombre` o `usr.new nombre` solos para introducir la contraseña sin eco. La contraseña no se imprime ni se almacena en historial. En scripts, los argumentos explícitos forman parte del propio contenido del archivo: el usuario debe proteger sus scripts y evitar secretos en ellos.

Ejemplos de filtros:

```ksl
proc | where owner guest | sort runtime --desc
log.audit | where kind SecurityViolation | select time actor target
svc | select id state ticks > /temp/services.tsv
```

La salida de las tablas se adapta al ancho, con celdas truncadas para legibilidad. `>` conserva el texto completo de cada celda en WVFS. `fs.read` devuelve una tabla de una celda; la consola escapa caracteres de control para preservar seguridad del terminal.
