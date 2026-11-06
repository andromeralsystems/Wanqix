# Seguridad y anillos

## Frontera de confianza

Wanqix es un proceso de usuario .NET. Las bibliotecas compiladas y el proceso anfitrión son confiables. Las entradas KSL, rutas, argumentos y usuarios virtuales no lo son. Ni los anillos ni PBKDF2 establecen un aislamiento contra otro proceso del mismo usuario de Windows, contra modificación de snapshots desde el anfitrión o contra código arbitrario cargado en el proceso. No se ofrece un loader de ensamblados, ejecución de CMD/PowerShell ni acceso al disco real desde KSL.

## Anillos

| Anillo | Emisión | Función |
|---|---|---|
| 0 | Solo código del supervisor | Estado, despacho, proceso PID 100 y región 0 protegidos; sin token emitible al usuario |
| 1 | Registro interno de servicios compilados | Tokens privados con capacidades fijadas en código; `metrics` usa WSCI |
| 2 | Login de usuario con rol administrativo vigente | Consola administrativa, con autorizaciones verificadas en cada operación |
| 3 | Sesión guest, login ordinario, procesos virtuales | Acceso limitado por capacidades, propietario y política del objeto |

Un usuario y su anillo son dimensiones diferentes. Conceder `admin` a una sesión Ring 3 no cambia su anillo: necesita autenticarse de nuevo para crear una sesión Ring 2. Revocar `admin` invalida sus capacidades administrativas inmediatamente, incluso en una sesión Ring 2 ya abierta. El anillo nunca se acepta como campo de KSL. No existen órdenes para convertirse en Ring 0, desactivar WSA o conceder capacidades arbitrarias.

Los tokens de sesión son GUID aleatorios emitidos por el kernel, validados contra una tabla privada. El contenido de una solicitud no puede fabricar un contexto. Al autenticar/cerrar sesión se revoca el token anterior. Sesiones ordinarias expiran a las 12 horas. Los tokens internos Ring 1 no se exponen por WSCI ni se guardan en snapshots.

## Políticas

Toda syscall valida sesión/estado, y cada gestor comprueba la capacidad requerida. WSA consulta roles actuales, registra autorización/rechazo y deniega capacidades desconocidas. Las operaciones administrativas también requieren Ring 2 o más privilegiado. Los gestores aplican propietario, anillo del objeto, cuota, rutas y transiciones válidas. El supervisor se protege aun ante root.

La lectura de procesos y memoria filtra recursos de otros usuarios; root puede inspeccionar metadatos protegidos. Modificar memoria/procesos de Ring 0 está prohibido para cualquier sesión emitida al usuario. Los procesos creados son siempre Ring 3 y reciben únicamente un subconjunto fijo de capacidades, sin heredar privilegios administrativos.

WVFS normaliza `.` y `..`, rechaza escape sobre `/`, caracteres de control, rutas Windows, barras inversas y tamaños excesivos. Lee/escribe por propietario; `filesystem.manage` autoriza administración transversal. Lectura pública se puede conceder por archivo/directorio propio; un ancestro privado sigue impidiendo acceso. `/temp` admite creación compartida, pero no sobrescritura de archivos ajenos. Los directorios iniciales protegidos no se borran ni mueven por syscalls. El borrado es de archivos o directorios vacíos, sin una opción recursiva destructiva.

Auditoría: `security.audit.read`; registro técnico: `log.read`. No existe una syscall de edición/borrado de auditoría. El registro es acotado y pierde entradas antiguas según retención; no es un almacén forense inmutable ni firmado. Las denegaciones en el servicio y en WSA quedan registradas con códigos, sin contraseñas. La salida escapa controles terminales para impedir secuencias ANSI/OSC introducidas desde archivos o argumentos.

Contraseñas: PBKDF2-HMAC-SHA256, 210 000 iteraciones, sal aleatoria de 32 bytes, derivación de 32 bytes, comparación de tiempo constante. No hay contraseña inicial universal: el usuario configura root. Cinco fallos por nombre en un minuto bloquean temporalmente nuevos intentos. No se promete defensa contra ataques offline al snapshot. Historial omite login, creación de usuarios y variables; desactívelo con `sys.config history false` si sus comandos contienen otros datos confidenciales.

## Pruebas negativas

La suite verifica que intentos de detener el supervisor, liberar su región, modificar archivos privados, usar PIDs/tokens inválidos, retener capacidades revocadas, escapar rutas y declarar anillos/capacidades en KSL no alteren el estado protegido. También prueba separación entre usuarios, cuotas, configuración inválida, dependencias de servicios y corrupción de snapshots.

La futura integración de módulos debe usar `IIsolatedModuleTransport` y procesos auxiliares con IPC validado. Esa interfaz es un contrato de extensión; **no** constituye un sandbox implementado. Cualquier extensión del código compilado forma parte de la base confiable y debe revisarse.
