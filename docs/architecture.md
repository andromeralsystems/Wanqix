# Arquitectura

La aplicación principal compone bibliotecas F#. El kernel acepta solicitudes tipadas por `IKernel.Call(session, Request)`; no depende de la terminal ni de KSL. `Request` es la IR de operaciones protegidas; `Instruction` agrega las operaciones locales del lenguaje. Los errores llevan código estable, mensaje y posición opcional. Las tablas usan celdas discriminadas `Text`, `Number` y `Flag`.

Dependencias (cada flecha significa «depende de»):

```text
Console -> Services, Terminal, Host.Windows
Terminal -> KSL -> Core
Host.Windows -> Core
Services -> Security, Memory, Processes, Storage -> Core
```

`Core` define `IKernel`, `Guard`, `IClock`, contratos de datos y errores; `Storage` define `IStateStore`. Se pueden sustituir reloj y backend mediante interfaces. Los gestores concretos separados permiten evolucionar las implementaciones; no hay todavía una interfaz pública por cada método de cada gestor. Las implementaciones confiables se componen desde `Kernel`.

## Escritura única y ejecución

Un inbox acotado admite hasta 1024 mensajes pendientes. Un hilo supervisor consume solicitudes bloqueando cuando está ocioso. Todos los cambios de estado, autenticación y ticks se serializan allí: un PID, un movimiento de árbol o una asignación de memoria se completan antes de admitir el siguiente cambio. No se mantiene un bloqueo global alrededor de la consola. El hilo dedicado evita interbloqueos por agotamiento del pool cuando varios clientes esperan una respuesta síncrona.

El anfitrión usa tareas asíncronas y `Task.Delay` cancelable para ticks; no hay sondeo activo. `--deterministic` desactiva ese reloj automático. Las pruebas inyectan `ManualClock` y avanzan con `Tick`. Prioridades 0–9 conceden 1–10 unidades de trabajo por turno, con rotación entre PIDs listos. Cada cuarta selección puede llevar una tarea a `Waiting`; el siguiente tick la despierta. Los estados `Running` y `Created` son transitorios dentro de una syscall/tick atómico. El trabajo finito actual es un contador, observable mediante `runtime` y `diagnostic`.

El supervisor protegido PID 100 representa el dispatcher y su ciclo de vida. Los PIDs de usuario comienzan en 101 y no se reutilizan durante el arranque. Hay hasta 1024 procesos activos y se depuran registros de procesos finalizados cuando la tabla alcanza 4096 entradas. Todos los procesos externos a la implementación interna son Ring 3, incluso si los inicia root. No reciben el token administrativo del shell.

WMM usa first-fit sobre regiones ordenadas. Comprueba tamaño, espacio contiguo, cuota por proceso, propietario y anillo. La liberación y finalización recuperan regiones. `sys.info` muestra consumo y hueco libre máximo del **modelo**, sin equipararlos a RAM física. La reserva 0 de 64 KiB pertenece al supervisor y no es liberable mediante WSCI.

## Arranque y cierre

`Cold -> Booting -> Running -> Stopping -> Stopped`. Un fallo al guardar durante cierre cambia a `Failed`. Se valida la configuración y el snapshot antes de construir el kernel; un fallo anterior a la construcción aborta el arranque sin emitir un éxito ficticio. Después se crean el journal, WSA, WMM, procesos, WVFS, servicios y homes de identidades, y se inician los servicios esenciales. El anfitrión crea la sesión y el shell cuando el kernel acepta llamadas.

El apagado detiene la admisión, finaliza procesos, libera regiones, detiene servicios en orden inverso, invalida sesiones y guarda. El anfitrión cancela y espera su tarea periódica. `Dispose` descarga el supervisor. Los fallos de persistencia se notifican y provocan salida no exitosa.

## Servicios y eventos

`LifecycleReporter` es un observador opcional de composición confiable, independiente de la terminal. Su operación `Run` emite inicio antes de ejecutar el trabajo, éxito después de que retorna y fallo al propagar una excepción. Los mensajes no se reconstruyen a partir de una lista fija ni se reproducen desde logs antiguos. Los servicios y procesos reportan únicamente las transiciones que ejecutan. El anfitrión conecta el observador a una presentación estilo systemd con reloj monotónico; no se registran contraseñas ni se exponen excepciones completas. Los errores de presentación no modifican la ejecución del kernel. El observador debe limitarse a presentar datos y no llamar de nuevo al kernel desde el hilo supervisor. Durante apagado, `Stopped` y el mensaje final solo aparecen tras guardar con éxito.

`logger` procesa eventos entregados por el bus; `clock` habilita avance de procesos; `wvfs` controla disponibilidad de archivos; `identity` controla login/gestión de cuentas; `metrics` consulta estadísticas mediante WSCI con un token Ring 1. Dependencias: clock/wvfs -> logger, identity -> wvfs, metrics -> clock+wvfs. Detener un servicio con dependientes activos se rechaza antes de modificar su estado. Iniciar uno arranca sus dependencias. Reiniciar conserva esas verificaciones; no fuerza la caída de dependientes.

El bus entrega registros de datos, sin callbacks de terceros. Cola máxima 1024; los descartes se contabilizan en `events.dropped`. El journal retiene las últimas 4096 entradas. Eventos incluyen arranque/cierre, procesos, cambios de archivos, presión de memoria, servicios y violaciones de seguridad. Los filtros separan auditoría de registros técnicos; ambos se persisten en el mismo snapshot con acceso lógico independiente.

## Persistencia

`state.v1.json` contiene versión 1, configuración, identidades con hashes PBKDF2, roles, archivos, servicios, historial, eventos y contador de arranques. Se escribe en temporal, se fuerza flush y se reemplaza atómicamente; el estado anterior queda en `state.v1.backup.json`. Se validan esquema, límites, árbol, nombres, credenciales y dependencias conocidas. Si falla el primario se valida el respaldo y se registra recuperación. La primera escritura tras recuperación conserva el respaldo válido. Si ambos fallan, se aborta sin reinicialización destructiva.

El directorio backend y sus ancestros no pueden ser enlaces/reparse points. Ninguna ruta virtual se usa para abrir un archivo anfitrión: solo existen los nombres de snapshot constantes. Este chequeo no pretende resistir sustituciones concurrentes de directorios por otro proceso con permisos del anfitrión. Solo se admite una instancia por directorio de datos (archivo de exclusión del backend).

Límites de almacenamiento: 64 MiB por snapshot, 10 000 entradas WVFS, 1 MiB de caracteres por archivo, 500 líneas de historial. Un snapshot es una unidad de persistencia, no una base de datos de gran escala. Las cuentas del anfitrión deben proteger el directorio de datos con sus permisos normales.
