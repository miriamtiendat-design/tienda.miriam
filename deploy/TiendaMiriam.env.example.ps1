# Opcional: configura esto solo si la caja debe sincronizarse con una API remota.
# Usa HTTPS y una cuenta con rol Cashier. No guardes credenciales en este archivo.
$env:TIENDA_CLOUD_API_URL = "https://api.example.com"
$env:TIENDA_CLOUD_USERNAME = ""
$env:TIENDA_CLOUD_PASSWORD = ""

# Impresoras Windows instaladas; deja vacío si no corresponde.
$env:TIENDA_THERMAL_PRINTER = ""
$env:TIENDA_KITCHEN_PRINTER = ""

# Opcionales. Por omisión, SQLite se crea en %LOCALAPPDATA%\TiendaMiriam\pos.db.
# $env:TIENDA_LOCAL_DB_PATH = "D:\TiendaMiriamData\pos.db"
# $env:TIENDA_TERMINAL_ID = "CAJA-01"

# No habilitar en producción. Los datos demo no representan el inventario real.
# $env:TIENDA_SEED_DEMO = "true"
