# Tienda Miriam

ERP/POS .NET 10 para una caja Windows, API PostgreSQL y panel web móvil con identidad JWT propia. Interfaz Glassmorphism Dark Premium en escritorio, panel móvil y cocina, moneda de reportes BOB y zona horaria predeterminada `America/La_Paz`.

## Módulos implementados

- **Escritorio Windows** ([ModuleWindows.cs](./src/TiendaMiriam.Pos/ModuleWindows.cs)): shell único con `NavigationManager` y pila de navegación; la barra superior ofrece Atrás, Inicio y accesos directos a Ventas, Inventario, Historial de Ventas, Proveedores y Reportes. El lanzador administrativo incluye **Cierres de caja · nube**, un historial consolidado para todas las terminales con filtros por intervalo, cajero y terminal. Las vistas se crean bajo demanda y permanecen ocultas al navegar, por lo que volver al POS conserva el carrito en curso. El tema oscuro combina superficies grafito, bordes sutiles, texto de alto contraste y acentos azul, púrpura y esmeralda; cada pantalla muestra un encabezado corto sin identificadores técnicos.
- **POS** ([MainForm.cs](./src/TiendaMiriam.Pos/MainForm.cs)): tarjetas táctiles adaptables en una cuadrícula sin scroll, con fotografía, nombre, precio en formato boliviano de dos decimales, disponibilidad calculada desde existencias o recetas fraccionarias y color configurable. Incluye categorías táctiles, búsqueda incremental, selección de cliente, nueva venta, guardar/recuperar con F9, cobro con F10, efectivo/QR/monedero/fiado, cambio grande, notas individuales por producto y un plano visual de mesas editable con estado, total y tiempo transcurrido; permite marcar comandas por cobrar, transferirlas y combinar cuentas.
- **Inventario, recetas y producción por lotes**: cinco pestañas distinguen **Insumos Crudos**, **Preparados del Día (Receta Directa)**, **Congelados / Por Lote**, **Artículos de Tienda / Reventa** y **Servicios**. Las recetas directas descuentan ingredientes al vender; la producción por lote descuenta insumos y aumenta el stock terminado, y luego cada venta resta sólo unidades congeladas. Los artículos de reventa restan su propia existencia y los servicios no descuentan inventario. El dueño puede cambiar la categoría con **Cambiar categoría**; el sistema valida que el producto tenga una configuración compatible (receta, existencia enlazada o ausencia de stock para servicios) y audita el cambio. Las cantidades de receta se convierten a unidades base (kg/g o l/ml), los movimientos y ajustes quedan auditados y la producción conserva sus lotes FEFO, costos y sincronización idempotente.
- **Persistencia local**: SQLite usa WAL y espera de bloqueo acotado para permitir lectores administrativos mientras opera la caja.
- **Inventario y recetas** ([SaleService.cs](./src/TiendaMiriam.Infrastructure/SaleService.cs)): ventas, pago, movimientos fraccionarios de insumos, costo de venta, ticket de cocina y outbox de sincronización dentro de una transacción local. Cada preparado descuenta porciones × cantidades de receta; las líneas que comparten insumos se consolidan antes de validar existencias/lotes, y cualquier fallo revierte venta, pago e inventario. Control optimista de concurrencia en existencias. Administra lotes con caducidad y consumo FEFO, recuentos físicos auditados, pagos personalizados/divididos y códigos EAN-13 de balanza (prefijo 20–29, cinco dígitos de SKU y cinco dígitos de peso en gramos).
- **Nube/API** ([Program.cs](./src/TiendaMiriam.Api/Program.cs)): PostgreSQL, JWT, rate limit al login, revocación de tokens al desactivar usuario/restablecer contraseña y políticas para `Administrator`, `Cashier` y `Kitchen`.
- **Operaciones administrativas**: catálogo/departamentos, cambios de precios y recetas auditados, listas de precios BOB y promociones temporales porcentuales o de precio fijo, creación/edición de inventario, alta y edición de proveedores con nombre, teléfono, contacto y dirección obligatorios; asociación de insumos/artículos y costos acordados, recepción de compras, historial y pagos a proveedores. En Inventario se pueden añadir, reemplazar, previsualizar y quitar imágenes de insumos, productos y recetas; en Proveedores se gestionan imágenes de cada proveedor. El selector acepta cualquier archivo y decodifica las imágenes que admite Windows, luego las optimiza y guarda en SQLite local. El historial permite filtrar por fecha y proveedor y resume compras y líneas del período. Las compras a crédito registran su vencimiento, y las deudas muestran el atraso en días; se admiten abonos parciales o liquidación en efectivo/QR desde una caja abierta. Cada pago queda en el historial y como gasto de la sesión, sin volver a afectar inventario. Los proveedores se listan aunque todavía no tengan compras. Las recepciones actualizan lotes, costo promedio, movimientos e inventario en una transacción. Incluye gastos, reportes con rango de minutos, clientes, abonos y fiados, anulación auditada.
- **Promociones y combos** ([PromotionBundleWindow.cs](./src/TiendaMiriam.Pos/PromotionBundleWindow.cs), [PromotionBundleService.cs](./src/TiendaMiriam.Infrastructure/PromotionBundleService.cs)): sólo el administrador puede crear combos, programar fechas, activar/desactivar o eliminarlos lógicamente. En el POS aparecen en una sección aparte con la foto del primer producto incluido; cada combo se cobra como una sola línea y descuenta dentro de la misma transacción todo el inventario de sus componentes y recetas. Precio y componentes quedan inmutables para preservar las ventas históricas; para cambiar la composición o el precio se crea otro combo. El historial conserva fecha de alta/baja, unidades vendidas, ingresos y descuento calculado con los precios vigentes al vender. Los cambios administrativos viajan por una outbox local y el catálogo cloud replica también los combos eliminados para poder conciliar ventas offline.
- **Finanzas por artículo**: costo unitario ponderado de compra para insumos, costo calculado de recetas, precio de venta, ganancia bruta y margen porcentual; cada línea vendida conserva un snapshot financiero inmutable que también se imprime en el ticket. Reportes muestra tarjetas destacadas y gráficos ligeros para ventas por día, ingresos frente a gastos, productos más/menos vendidos, ventas por hora, métodos de pago y utilidad diaria. El Administrador puede ocultarlos; la preferencia se guarda en SQLite. El PDF incluye una página de gráficos y hay exportación XLSX con siete gráficos editables y sus datos. Los reportes distinguen ganancia bruta (`ingresos − costo de ventas`), utilidad neta (`ganancia bruta − gastos`) e inversión recibida en compras. La inversión se informa por separado y no se resta otra vez del resultado, ya que el costo de ventas reconoce los insumos consumidos.
- **Auditoría de cambios**: el Administrador tiene una pantalla con filtros por fecha, usuario, acción, entidad, terminal y texto, y puede exportar a PDF/XLSX. Los eventos se guardan junto con la transacción local para no perderlos sin internet y su envío a la nube se reintenta en segundo plano mediante una outbox; incluye actor, terminal, valores anterior/nuevo y observación cuando está disponible. Los logs no se pueden alterar o borrar mediante EF y quedan registrados los intentos bloqueados. La retención se configura entre 30 y 3650 días (730 por defecto), se sincroniza con la nube y se purga a diario; cada purga conserva todo lo que todavía no se ha sincronizado.
- **Cocina** ([kitchen.html](./src/TiendaMiriam.Api/wwwroot/kitchen.html)): cola de comandas y estados Queued → Preparing → Ready → Served, sondeo cada 3 segundos; incluye mesa y nota especial de preparación. El POS puede imprimir tickets de venta y cocina usando impresoras Windows configuradas.
- **Panel móvil** ([index.html](./src/TiendaMiriam.Api/wwwroot/index.html)): resumen, gráfico de las últimas 24 horas y tendencia semanal, además de ventas mensuales, por hora y productos más vendidos; usuarios, stock, clientes, compras, gastos, reportes, fotografías y anulaciones; impresión desde el navegador. La analítica detallada se expone en `GET /api/admin/analytics`, sólo para Administrador y con rangos de hasta 366 días en la zona horaria del negocio.
- **Despliegue Render**: [Dockerfile](./Dockerfile) y [render.yaml](./render.yaml), con PostgreSQL administrado y HTTPS del servicio web.

## Ejecutar en desarrollo

Requiere .NET SDK 10. La API utiliza PostgreSQL por defecto; en desarrollo también se puede configurar SQLite.

```powershell
dotnet restore .\TiendaMiriam.sln
dotnet build .\TiendaMiriam.sln
dotnet test .\TiendaMiriam.sln
```

### Publicar paquete de escritorio para Windows x64

Desde la raíz del proyecto, ejecuta:

```powershell
dotnet publish .\src\TiendaMiriam.Pos\TiendaMiriam.Pos.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:PublishTrimmed=false `
  -o .\artifacts\TiendaMiriam-win-x64
```

El resultado es una aplicación autocontenida y de archivo único; el usuario no necesita instalar .NET. Para crear una plantilla SQLite vacía dentro del paquete y luego comprimirlo:

```powershell
$package = (Resolve-Path .\artifacts\TiendaMiriam-win-x64).Path
$env:TIENDA_LOCAL_DB_PATH = Join-Path $package "pos.db"
& (Join-Path $package "TiendaMiriam.exe") --initialize-database
if ($LASTEXITCODE -ne 0) { throw "No se pudo inicializar la plantilla SQLite." }
Remove-Item Env:\TIENDA_LOCAL_DB_PATH
Copy-Item .\deploy\INSTRUCCIONES.txt $package
Copy-Item .\deploy\TiendaMiriam.env.example.ps1 $package
Compress-Archive -Path (Join-Path $package "*") `
  -DestinationPath .\artifacts\TiendaMiriam-win-x64.zip -CompressionLevel Optimal -Force
```

La plantilla incluida no contiene catálogo, credenciales ni datos ficticios; en el primer inicio normal se copia a `%LOCALAPPDATA%\TiendaMiriam\pos.db` para que las ventas locales sean escribibles sin permisos de administrador. Configura el catálogo real/sincronización antes de operar; conserva las credenciales fuera del paquete y configura TLS HTTPS.

API local, después de configurar su entorno:

```powershell
$env:ConnectionStrings__Cloud = "Host=localhost;Port=5432;Database=tienda_miriam;Username=postgres;Password=..."
$env:Jwt__SigningKey = "<secreto aleatorio de al menos 32 bytes>"
$env:Bootstrap__AdminPassword = "<secreto inicial de al menos 14 caracteres>"
dotnet run --project .\src\TiendaMiriam.Api
```

Para probar el API sin Postgres, establecer además `$env:Database__Provider = "Sqlite"` y usar `Data Source=.\tienda-api.db` en `ConnectionStrings__Cloud`. La **primera ejecución** crea `owner` con rol `Administrator`; `Bootstrap__AdminPassword` es obligatorio si la tabla de usuarios está vacía y se debe retirar del entorno después de inicializar la cuenta. El panel web se sirve desde la API; también se puede abrir `/kitchen.html`. Usuarios y contraseñas se administran en el panel. Las contraseñas se guardan como hashes con `PasswordHasher`; los JWT expiran a las 4 horas y la versión de autenticación permite revocarlos.

## Caja y sincronización

La caja guarda la base local en `%LOCALAPPDATA%\TiendaMiriam\pos.db`. El POS sincroniza catálogo, recetas, clientes, ventas y estado de caja automáticamente cuando vuelve la red. Configura:

- `TIENDA_CLOUD_API_URL`: URL HTTPS de la API. HTTP se rechaza salvo para loopback (`localhost`, `127.0.0.1` o `::1`) cuando también se establece `TIENDA_ALLOW_INSECURE_LOCAL_API=true`; ese permiso es sólo para desarrollo local y no habilita HTTP en una dirección LAN.
- `TIENDA_ALLOW_INSECURE_LOCAL_API=true`: excepción explícita para conectar por HTTP a una API en la misma máquina durante desarrollo; nunca activarla en cajas de producción.
- `TIENDA_CLOUD_USERNAME` y `TIENDA_CLOUD_PASSWORD`: cuenta de rol `Cashier`; el POS mantiene el JWT sólo en memoria y lo renueva al vencer. Como alternativa, `TIENDA_CLOUD_ACCESS_TOKEN` proporciona un token fijo, que debe rotarse antes de expirar.
- `TIENDA_CLOUD_ADMIN_USERNAME` y `TIENDA_CLOUD_ADMIN_PASSWORD` (o `TIENDA_CLOUD_ADMIN_ACCESS_TOKEN`): credenciales cloud opcionales de rol `Administrator` para el historial consolidado. Si no se especifican, la pantalla reutiliza las credenciales cloud generales; la API sólo permite la consulta a un Administrador.
- `TIENDA_TERMINAL_ID`: identificador estable de la caja (por defecto, el nombre de equipo).
- `TIENDA_THERMAL_PRINTER` y `TIENDA_KITCHEN_PRINTER`: nombres de impresoras Windows instaladas; si se omiten, el flujo no envía trabajos.
- `TIENDA_SEED_DEMO=true`: carga datos locales de demostración en una base vacía. No utilizar para cajas reales.
- `TIENDA_LOCAL_DB_PATH`: ruta opcional de SQLite para aislar una demo de la base de caja usual `%LOCALAPPDATA%\TiendaMiriam\pos.db`.
- `TIENDA_START_MODULE=sales`: abre directamente la pantalla de venta en vez del lanzador principal.

Las ventas, pagos, movimientos de inventario y comandas se registran primero en SQLite dentro de una transacción atómica y después se sincronizan. El catálogo descargado incluye tarifas y promociones para permitir cotizar localmente sin conexión; la nube valida la tarifa y la promoción que correspondían a la hora registrada y deja la venta pendiente si el precio ya no coincide. El panel administrativo permite crear listas en `/api/admin/price-lists`, asignar un precio por producto y programar promociones; todos los importes se expresan únicamente en BOB. El lanzador permite abrir la caja y los módulos administrativos simultáneamente en una misma instalación, aprovechando WAL. Las órdenes de mesa se conservan localmente y se sincronizan con control de versión cuando hay conexión; no hay notificaciones push ni bloqueo en tiempo real, por lo que varias cajas pueden ver conflictos si editan la misma mesa simultáneamente.

### Demostración de escritorio

Abre PowerShell en la carpeta del proyecto y usa una base aparte para no mezclar datos ficticios con la caja habitual:

```powershell
$demoRoot = Join-Path (Get-Location) "demo-data"
New-Item -ItemType Directory -Force $demoRoot | Out-Null
$env:TIENDA_LOCAL_DB_PATH = Join-Path $demoRoot "pos-demo.db"
$env:TIENDA_SEED_DEMO = "true"
dotnet run --project .\src\TiendaMiriam.Pos\TiendaMiriam.Pos.csproj
```

El menú integrado permite abrir Ventas, Inventario, Historial de Ventas, Proveedores y Reportes dentro de la misma ventana; usa **Volver** o **Inicio** en la barra superior para navegar sin perder el carrito activo. El sembrado idempotente incluye un catálogo de demostración con artículos simples, insumos fraccionarios, recetas, productos congelados, recargas, servicios y artículos varios, además de clientes ficticios, proveedores, compras, gastos y ventas de ejemplo. Todos los datos están marcados como demo; no actives el sembrado en la base operativa. En el menú usa **Cambiar logotipo** para elegir una imagen local PNG, JPEG, BMP, GIF o TIFF (máximo 5 MB); una imagen de internet debe descargarse antes y un teléfono debe estar conectado/montado en Windows. En Inventario selecciona cualquier insumo, producto o receta y usa **Añadir / cambiar imagen**; en Proveedores selecciona el proveedor y usa el control de imagen de esa pantalla. Los archivos de imagen se optimizan a PNG y se guardan en SQLite local; el sistema puede abrir cualquier formato que los decodificadores de imagen instalados en Windows reconozcan.

Para sembrar también una instancia API/SQLite separada, usa otra terminal:

```powershell
$env:Database__Provider = "Sqlite"
$env:ConnectionStrings__Cloud = "Data Source=.\demo-data\api-demo.db"
$env:Jwt__SigningKey = "Solo-Demo-Local-signing-key-con-32-bytes"
$env:Bootstrap__AdminPassword = "Demo-Only-Password-2026!"
$env:Demo__Seed = "true"
dotnet run --project .\src\TiendaMiriam.Api\TiendaMiriam.Api.csproj
```

El seed se aplica una sola vez por base mediante su marcador; prepara una ruta de base nueva para una demo limpia. No configures `TIENDA_SEED_DEMO` ni `Demo__Seed` en producción.

Si se desea arrancar directamente en caja en lugar del menú de módulos, establece `$env:TIENDA_START_MODULE = "sales"` antes del comando `dotnet run`.

El catálogo inicial se configura en el panel. Cuando la caja recupera conexión lo descarga y lo persiste localmente. Las ventas offline se mantienen en outbox con ID idempotente; el POS consulta cuáles ya fueron aceptadas para recuperarse de respuestas perdidas. Si un precio o fingerprint de receta cambió en nube, la API no recalcula ni cobra con datos diferentes: conserva la venta pendiente para conciliación.

## Render

Render Blueprint usa PostgreSQL y genera secretos de JWT y de administrador inicial. Revisa el plan de base PostgreSQL y el valor de `Bootstrap__AdminPassword` en Render al terminar la inicialización; elimina el secreto inicial y establece una contraseña nueva para `owner`. Nunca guardes secretos en el repositorio. La URL pública debe usar HTTPS.

## Pruebas actuales

`dotnet test .\TiendaMiriam.sln` contiene pruebas SQLite para atomicidad/despiece, idempotencia, recuperación de stock y auditoría de anulaciones, bloqueo de anulación después del inicio de cocina, costo promedio de compra, reportes, conflictos de receta y listas/promociones, hash/bloqueo de acceso local, arqueo/cierre de caja, notas por línea y versiones/estados/eliminación segura de órdenes de mesa. La API incluye solicitudes de ejemplo para tarifas y promociones en [CommercePricing.http](./src/TiendaMiriam.Api/CommercePricing.http).

## Operación local, caja y mesas

- **Pantalla de espera y animaciones**: desde Inicio, el Administrador abre **Pantalla de espera y animaciones…** para configurar el tiempo sin actividad (20 minutos por defecto, de 1 a 240), el nombre y mensaje de bienvenida, el reloj de 24 horas, el uso de contraseña al volver y las animaciones. La pantalla cubre temporalmente la ventana sin cerrar ni reiniciar el POS, por lo que conserva el carrito y la mesa activos; usa el logotipo actual del negocio. Un clic o tecla la cierra; si se activa la contraseña, se valida la del usuario local que dejó abierta la sesión. Las preferencias se guardan en la base SQLite local. Las animaciones son breves y opcionales; incluyen transiciones entre vistas, efecto al añadir un producto, confirmación del cobro y respiración sutil del logotipo.
- La aplicación de escritorio solicita un usuario local. En la primera ejecución se crea el administrador local; desde **USUARIOS** el administrador crea cajeros, restablece contraseñas y desactiva cuentas. Las contraseñas locales se almacenan como PBKDF2-SHA256 con sal aleatoria y cinco intentos fallidos activan un bloqueo temporal. El cajero accede al POS y al arqueo; inventario, reportes, proveedores e historial administrativo se reservan al administrador. Esta identidad local no es una sesión de la API web.
- La operación prevista tiene una sola terminal de ventas y un panel administrativo separado para el dueño. Se impide iniciar dos procesos POS al mismo tiempo en el mismo equipo; el panel administrativo no registra ventas ni compite por stock. La base local de la caja es la autoridad mientras está offline y las ventas sincronizadas actualizan el stock del servidor mediante la transacción de venta existente.
- Cada cobro se confirma localmente junto con sus movimientos de stock y su mensaje de outbox. Al recuperar la red, Ventas intenta sincronizar inmediatamente después del cobro y vuelve a intentarlo cada 15 segundos mientras la aplicación permanezca abierta, incluso al navegar a otro módulo; el catálogo descendente incluye productos, recetas, existencias y lotes, y conserva los descuentos de ventas aún pendientes sin duplicarlos.
- Antes del primer cobro de cada caja se abre una sesión con fondo inicial. **ARQUEO / CIERRE** muestra ventas, recargas de monedero, abonos de fiado y gastos registrados durante el turno, distinguiendo efectivo de QR. El esperado se calcula como fondo + efectivo recibido en ventas + recargas y abonos en efectivo - vuelto - gastos pagados en efectivo; QR se informa sin alterar el efectivo de caja. Los movimientos antiguos sin método de pago se consideran efectivo. Para cerrar se registra el efectivo contado y se exige observación si hay diferencia. Los cierres y aperturas quedan auditados localmente por terminal.
- Al cerrar un turno, el arqueo queda guardado localmente y se envía idempotentemente a la API cloud. Si no hay internet, permanece en una cola local y el shell reintenta automáticamente al recuperar conexión. **Cierres de caja · nube** consulta exclusivamente el historial de la base cloud, consolidado entre terminales, con filtros de fechas, cajero y terminal; muestra fondo inicial, esperado, contado y diferencia. Si hay más de 1000 resultados, se indica acotar el intervalo.
- El plano de mesas muestra botones grandes en verde (libre), rojo (ocupada) y amarillo (por cobrar), con total y tiempo desde la apertura. Cada producto admite su propia nota, que se conserva en comandas en espera, tickets de cocina y recibos. Las acciones de transferir y combinar validan la ocupación y la versión; las combinaciones conservan líneas, notas y estado de cobro, y rechazan mezclar órdenes incompatibles para evitar pérdida de información. Al cobrar, la venta cierra la orden pendiente. La sincronización API conserva órdenes con control de versión; si dos clientes editan la misma orden, el conflicto se informa en vez de sobrescribir cambios. Sin conexión, las actualizaciones y eliminaciones pendientes quedan locales hasta que se recupera la red.

## Límites y requisitos antes de una operación comercial

Este proyecto ya es un prototipo integrado que compila y ejecuta flujos de demostración; **no certifica un despliegue comercial de alta disponibilidad**. En particular:

- No se admite una segunda terminal POS para vender en paralelo. El bloqueo de instancia protege contra aperturas duplicadas en la misma computadora; el despliegue debe mantener la caja en un único equipo dedicado. La sincronización exige stock suficiente en el servidor antes de aceptar la venta y deja conflictos visibles si el panel cambia cantidades mientras la caja permanece offline.
- Si los costos históricos aún no existen para una venta antigua, el reporte devuelve utilidad `null` e indica ventas sin snapshot, en vez de declarar una ganancia engañosa.
- El alcance de Tienda Miriam es registrar ventas y emitir recibos; no emitirá facturas ni implementará facturación tributaria o impresión fiscal certificada. Siguen pendientes la integración con operadores externos de recargas/móviles y pasarelas QR reales. Las recargas se pueden representar como productos/servicios, pero no se conectan a un proveedor.
- El arqueo es local por terminal y consolida los movimientos que están en la base SQLite local y cuya fecha/hora cae dentro del turno. Los gastos, abonos y recargas registrados exclusivamente en la base cloud no se descargan automáticamente al POS y, por ello, no aparecen en este arqueo local. Los cierres tampoco se consolidan todavía en nube.
- El panel web requiere internet; compras, gastos, mantenimiento de catálogo, administración de clientes, usuarios y recetas son operaciones online. Las ventas/consumos/retención operan con datos locales, pero no todas las operaciones de administración son offline.
- Los módulos de escritorio consultan SQLite local. Compras y gastos registrados sólo en la nube no se replican automáticamente a esa base; para un consolidado cloud, usa el reporte del panel web hasta que se implemente la sincronización descendente de administración.
- El sistema usa `EnsureCreated` más creación idempotente de tablas complementarias para inicializar la base existente; **no hay migraciones versionadas**. Antes de evolucionar esquemas en producción se deben adoptar migraciones EF Core y ensayar la actualización/restauración de respaldos.
- La caja incluye una acción para crear una copia local consistente de SQLite y verificar su integridad. No hay restauración desde la interfaz ni un plan automatizado de retención, almacenamiento externo o PITR: se deben configurar por separado, proteger el archivo (contiene datos personales y ventas), probar restauraciones y probar impresoras físicas y concurrencia con la topología real.

No se debe usar información de demostración como inventario comercial. El entorno de producción necesita un proveedor de base de datos estable, configuración de identidad, respaldo y procedimientos de conciliación.
