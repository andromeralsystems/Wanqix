# KSL 1.0

KSL se interpreta íntegramente dentro de Wanqix. No se pasa texto a CMD, PowerShell ni otro shell anfitrión. El pipeline de implementación es `Lexer -> Token[] -> Parser -> Invocation/Pipeline AST -> CommandRegistry -> Instruction -> Request/LocalOperation -> WSCI o sesión`.

## Gramática inicial

```text
script       := separator* pipeline ((separator+ | '&&') pipeline)* separator*
pipeline     := invocation ('|' invocation)* ('>' argument)?
invocation   := identifier argument* | '@' identifier '::' identifier '{' fields '}'
fields       := separator* (identifier ':' argument separator+)*
separator    := ';' | newline
argument     := bare-word | quoted-string
```

El último campo de un bloque puede omitir `;`. Se admiten comentarios `#` hasta fin de línea fuera de comillas. Identificadores de campos usan puntos (`memory.limit`). Comillas simples/dobles mantienen espacios y operadores como contenido. Escapes: `\n`, `\r`, `\t`, `\\`, `\"`, `\'`. Una barra inversa fuera de una cadena es texto de argumento, pero WVFS la rechaza en rutas. No hay interpolación de comandos ni expresiones del anfitrión. El lexer y el parser informan posiciones de caracteres base 0.

## Operadores

- `;` / salto de línea: siguiente pipeline incondicional, incluso tras fallo. Todos los resultados se devuelven al invocador.
- `&&`: ejecutar el pipeline siguiente solo si el anterior ejecutado tuvo éxito. Una cadena de saltos conserva el fracaso hasta la siguiente separación incondicional.
- `|`: pasa una tabla de celdas tipadas al siguiente filtro (`where`, `sort`, `select`, `take`). No convierte una fila en argumentos privilegiados ni inicia procesos externos. Un comando que no consume tabla rechaza su uso en esa posición.
- `>`: serializa una tabla TSV a un archivo **virtual**, con tabuladores/nuevas líneas de las celdas escapados. Comprueba autorización normal de escritura. Sustituye el contenido anterior; no hay append.

`where columna valor` compara números/booleanos como tipos; compara texto sin distinguir mayúsculas. `sort columna [--desc]` usa el tipo de la celda. `select` valida columnas y `take` exige un entero no negativo. Operadores dentro de comillas nunca son ejecutados. El valor expandido de una variable sigue siendo un argumento: jamás se vuelve a analizar como código.

## Variables y alias

```ksl
set work job.alpha
proc.spawn $work --steps 10
set directory /temp
fs.write ${directory}/notes.txt "texto"
alias jobs proc.list
jobs | sort pid
```

Variables y alias viven solamente en la sesión. Ambos tipos de comillas permiten expansión `$name`/`${name}`. Variables inexistentes son errores. Un alias sustituye solo el **nombre** de un comando, no incluye argumentos ni código. Se detectan ciclos y no se pueden reemplazar órdenes registradas. Login/logout limpian variables y alias. Las variables usan hasta 64 caracteres con letras ASCII, números y `_`, empezando por letra o `_`. Los alias admiten además `.` y `-`.

## Sintaxis estructurada

| Instrucción | Campos admitidos | Equivalente rápido |
|---|---|---|
| `@process :: spawn` | `identity` obligatorio; `priority`, `memory.limit`, `steps` opcionales | `proc.spawn` |
| `@process :: terminate` | `pid` | `proc.end` |
| `@memory :: inspect` | `region: all`, `format: detailed` opcionales | `mem.map` |
| `@memory :: allocate` | `pid`, `size` | `mem.alloc` |
| `@memory :: release` | `region` | `mem.free` |
| `@filesystem :: read` | `path` | `fs.read` |
| `@filesystem :: write` | `path`, `content` | `fs.write` |
| `@service :: start` / `stop` | `name` | `svc.start` / `svc.stop` |
| `@kernel :: inspect` | ninguno | `sys.info` |

Campos duplicados, desconocidos o intentos de declarar `ring`/`capabilities` son errores. `identity` de spawn es un nombre de trabajo, jamás un cambio de identidad propietaria. Los valores por defecto de spawn son prioridad 4, límite 64 MiB y 100 pasos. Las dos formas generan la misma unión discriminada `Request` y atraviesan el mismo WSCI.

Los tamaños usan enteros no negativos con `B`, `KiB`, `MiB` o `GiB`, sin distinguir mayúsculas. Se detecta overflow. La memoria y las operaciones correspondientes exigen valores positivos dentro de sus cuotas.

## Scripts y extensiones

`run /ruta/script.ksl` lee desde WVFS bajo la sesión actual. Un script no adquiere permisos adicionales. Recursión máxima 16 niveles y presupuesto compartido de 10 000 invocaciones por ejecución. Tamaño de entrada máximo 1 MiB de caracteres. La cancelación se comprueba entre instrucciones; no se ejecuta código arbitrario de terceros.

`CommandRegistry.Register` admite definiciones de comandos confiables que producen una `Instruction`; extender el intérprete requiere código compilado. Los comandos de scripts no pueden registrar handlers .NET. El AST es independiente del backend y de la presentación. No hay bucles, funciones de usuario, shell escaping anfitrión ni funciones vacías que aparenten ejecutar esas características.

Errores principales: `WQX-KSL-001` para sintaxis/resolución con posición, `WQX-KSL-002` para límites de ejecución, `WQX-ARG-001` para argumentos, `WQX-SEC-001` para acceso y `WQX-KRN-006` para cancelación. Los errores de otros subsistemas conservan sus códigos.
