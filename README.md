# Wanqix 1.0

Kernel **simulado** de alto nivel, escrito en F# 10 y .NET 10. Aplicación de consola para Windows 10/11 y Windows Terminal; los subsistemas no dependen de Windows. Las operaciones modifican procesos, regiones lógicas, archivos virtuales, sesiones y servicios reales del modelo.

## Compilar, ejecutar y comprobar

Repositorio público: [andromeralsystems/Wanqix](https://github.com/andromeralsystems/Wanqix).

```powershell
git clone https://github.com/andromeralsystems/Wanqix.git
cd Wanqix
```

Requisitos: SDK .NET 10.0.400 o compatible y, opcionalmente, Visual Studio 2026 con F#. Abra `Wanqix.slnx`.

```powershell
cd A:\Wanqix\1.0
dotnet build Wanqix.slnx -c Release
dotnet run --project src/Wanqix.Console -c Release
dotnet run --project tests/Wanqix.Tests -c Release
dotnet publish src/Wanqix.Console -c Release -r win-x64 --self-contained false -o publish/win-x64
.\publish\win-x64\Wanqix.exe
```

Las pruebas son un ejecutable F# sin paquetes de terceros: devuelven 0 solamente si pasan las aserciones. `dotnet test` no sustituye este comando. `tools/verify.ps1` compila Debug/Release, ejecuta las pruebas y verifica el CLI con almacenamiento temporal.

En el primer arranque interactivo se pide crear la contraseña virtual de root, sin eco, con 12–1024 caracteres. La sesión comienza como **guest, Ring 3**. Para administrar:

```ksl
usr.login root
usr.info
help
```

La terminal pide la contraseña de forma oculta cuando `usr.login nombre` o `usr.new nombre` se escriben solos. Root se autentica como **Ring 2**, nunca Ring 0. Ctrl+D cierra la consola de manera ordenada; `sys.off` requiere administración. Ctrl+C cancela la línea o el script activo. `sys.reboot` guarda y vuelve a arrancar en modo interactivo; en modo de comandos guarda, cierra y deja el siguiente arranque al invocador.

Modo reproducible/no interactivo:

```powershell
# Proporcione la contraseña mediante el entorno de su proceso; no la guarde en el repositorio.
# WANQIX_ROOT_PASSWORD se usa para inicializar root o autenticar --admin.
dotnet run --project src/Wanqix.Console -c Release -- --admin --deterministic --command 'proc.spawn worker --steps 10; mem.alloc 101 1MiB; sys.tick 2; proc; mem'
dotnet run --project src/Wanqix.Console -c Release -- --admin --deterministic --script examples/scripts/demo.ksl
```

`--data PATH` selecciona otro directorio de snapshots. Sin esta opción se usa `%LOCALAPPDATA%\Wanqix\1.0`. `--script` lee **únicamente** el archivo anfitrión indicado explícitamente; `run` de KSL lee exclusivamente WVFS. Sin `--admin` el modo no interactivo también comienza como guest. `--no-color` elimina colores. La entrada redirigida admite una instrucción completa por línea; `--script` admite bloques multilínea. Un error produce código de salida 1. Los scripts se analizan por completo antes de ejecutar; sus operaciones no forman una transacción global.

## Diagnóstico de arranque, apagado y reinicio

La consola muestra cada operación real con tiempo transcurrido, fase (`BOOT`, `SVC`, `STOP`, `REBOOT`, `HOST`) y estado: `[ .... ]` al comenzar, `[ OK ]` solo al completar, `[ INFO ]` para contexto y `[FAILED]` si falla. Incluye carga/validación del snapshot, subsistemas, servicios individuales, sesiones, procesos activos con PID, reclamación de memoria, persistencia y cierre del supervisor/backend. Un fallo de guardado no anuncia apagado completado. Una solicitud denegada no inicia una secuencia de cierre.

Los mensajes se emiten en vivo, sin pausas artificiales ni comandos Linux ficticios. Se refieren al kernel virtual y a las tareas del anfitrión que Wanqix controla. Usan stderr para mantener stdout disponible para resultados KSL y admiten `--no-color` y redirección. En reinicio interactivo se muestra el cierre seguido del nuevo arranque; `--command sys.reboot` conserva su comportamiento de cerrar y devolver control al invocador.

## Ejemplo KSL

```ksl
fs.go /users/guest
fs.new projects
fs.write projects/readme.txt "Hola desde Wanqix"
proc.spawn worker --priority 4 --memory 16MiB --steps 100
mem.alloc 101 1MiB
proc | where state Ready | sort pid --desc | select pid name
fs.read projects/readme.txt > projects/result.tsv
@process :: spawn {
    identity: "worker.alpha";
    priority: 4;
    memory.limit: 16MiB;
    steps: 50;
}
```

El PID de ejemplo presupone un arranque nuevo. Consulte `proc` para usar el PID emitido en su sesión. `identity` en el bloque es el **nombre del trabajo**, no permite elegir propietario ni suplantar una identidad.

## Estructura

```text
src/Wanqix.Core          contratos, tipos, reloj, errores y registro
src/Wanqix.Security      WSA, roles, sesiones y autenticación PBKDF2
src/Wanqix.Memory        WMM: regiones, cuotas y fragmentación lógica
src/Wanqix.Processes     procesos y planificador cooperativo
src/Wanqix.Storage       WVFS y snapshots JSON atómicos
src/Wanqix.Services      servicios, bus de eventos, WSCI y ciclo de vida
src/Wanqix.KSL           lexer, parser, AST, IR, registro y ejecución
src/Wanqix.Terminal      editor de línea y presentación segura
src/Wanqix.Host.Windows  adaptación de consola y ubicación de datos
src/Wanqix.Console       composición y punto de entrada Wanqix.exe
tests/Wanqix.Tests       pruebas de subsistemas, seguridad e integración
docs/                   arquitectura, seguridad, KSL, comandos y verificación
examples/scripts/       scripts ejecutables
wanqix/                 proyecto inicial compatible con el nuevo punto de entrada
```

La plantilla original se conserva en `docs/original-template`. El proyecto del directorio `wanqix` compila el mismo punto de entrada mediante un archivo vinculado; puede seguir ejecutándose desde la ubicación original.

## Licencia

Wanqix se distribuye gratuitamente bajo la [licencia MIT](LICENSE). Se permite usarlo, modificarlo, crear forks y redistribuirlo, incluso para uso comercial o como parte de productos comerciales. Las copias y porciones sustanciales deben conservar el aviso de copyright y el texto de la licencia. Se proporciona sin garantía.

## Alcance y límites

- Cuatro anillos lógicos: supervisor interno Ring 0, servicios compilados Ring 1 con tokens propios, administración autenticada Ring 2 y trabajos/KSL Ring 3. No se puede declarar un anillo desde KSL.
- Los procesos ejecutan contadores cooperativos finitos con prioridad ponderada, espera, suspensión, cancelación y reclamación de memoria. No ejecutan binarios arbitrarios, código .NET externo ni un conjunto general de instrucciones de CPU.
- La memoria es un mapa lógico de metadatos; no almacena bytes de RAM ni reserva el tamaño simulado. Reserva inicial protegida: 64 KiB del supervisor.
- WVFS está en un snapshot aislado. Contenido de texto, permisos por propietario y lectura pública, administración explícita; sin ACL POSIX completa, enlaces virtuales, handles abiertos ni archivos binarios.
- Roles configurados `user` y `admin`; grupos persistentes informativos. Gestión de usuarios/roles y revocación inmediata funcionales; edición de grupos/roles personalizados, cambio de contraseña y eliminación de cuentas quedan para versiones posteriores.
- Cinco servicios compilados (`logger`, `clock`, `wvfs`, `identity`, `metrics`), con dependencias e inicio automático. El registro se persiste y valida; añadir implementaciones requiere recompilar. No se cargan ensamblados externos. Una interfaz documenta la futura frontera IPC; no existe un sandbox externo implementado.
- Persisten configuración, identidades, roles, archivos, registro de servicios, historial habilitado y hasta 4096 eventos. No se reanudan procesos ni regiones de RAM después del apagado. Las sesiones expiran a las 12 horas y no se persisten.
- KSL ofrece las dos sintaxis, alias de nombres de comandos, variables, filtros, proyección, ordenamiento, scripts, `&&`, `|` y `>`. Sin bucles, funciones, expresiones aritméticas, expansión del shell anfitrión ni transacciones de scripts. La sintaxis estructurada incluye las operaciones documentadas, no una traducción automática de todo comando futuro.
- La consola ofrece Tab cíclico, sugerencias por coincidencia, historial, flechas, Home/End, edición y tablas. El redimensionamiento se refleja al siguiente evento de edición. El cálculo de ancho usa caracteres .NET: emojis/caracteres de doble ancho pueden desalinear líneas largas. No es un emulador de terminal completo.
- Un único escritor del estado garantiza atomicidad de cada syscall. Snapshot completo después de cada instrucción interactiva/script de entrada y durante apagado: adecuado para 1.0, no para volúmenes de disco grandes.

**Seguridad real:** el anfitrión y los componentes compilados son de confianza. Los controles internos no aíslan al proceso .NET de Windows ni resisten a quien pueda modificar su memoria o sus snapshots desde el anfitrión. No hay elevación de Windows, ejecución de comandos anfitriones, telemetría remota ni carga de módulos no confiables. Véase [security-rings.md](docs/security-rings.md) para garantías y límites precisos.

Documentación: [arquitectura](docs/architecture.md), [KSL](docs/ksl-specification.md), [comandos](docs/commands.md), [verificación](docs/verification.md). Licencia MIT.
