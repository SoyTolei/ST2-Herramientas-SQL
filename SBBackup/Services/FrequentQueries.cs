namespace SBBackup.Services;

/// <summary>
/// Catálogo de consultas/queries de uso frecuente, agrupadas por categoría.
/// Se cargan en el editor con un clic. Las que requieren editar el nombre de la
/// base a mano traen un aviso (<see cref="Item.Note"/>).
/// <see cref="Item.SuccessMessage"/> se muestra al finalizar (placeholders:
/// {base}, {filas}, {filasDevueltas}).
/// <see cref="Item.UsesManagerDatabase"/> fuerza la ejecución sobre MANAGER.
///
/// Orden de <see cref="Groups"/>: "Mantenimiento de base" va último a propósito (son
/// las opciones menos usadas del día a día / más delicadas). El resto está ordenado
/// para que el menú de 2 columnas quede parejo en altura (grupos de tamaño similar
/// emparejados entre sí). Si agregás/quitás ítems, tratá de no dejar un grupo mucho
/// más largo que su pareja de fila.
/// </summary>
public static class FrequentQueries
{
    public sealed record Item(
        string Name,
        string Sql,
        string? Note = null,
        bool UsesSelectedDatabase = false,
        string? SuccessMessage = null,
        bool UsesManagerDatabase = false);

    public sealed record Group(string Name, IReadOnlyList<Item> Items);

    public static string FormatSuccessMessage(
        string template,
        string database,
        int rowsAffected,
        int rowsReturned)
    {
        return template
            .Replace("{base}", database, StringComparison.OrdinalIgnoreCase)
            .Replace("{filas}", rowsAffected.ToString("N0"), StringComparison.OrdinalIgnoreCase)
            .Replace("{filasDevueltas}", rowsReturned.ToString("N0"), StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<Group> Groups { get; } =
    [
        new Group("Consultas de gestión",
        [
            new Item(
                "Listado de RPT por circuito/talonario",
                "USE [<baseDeDatos>]\r\n" +
                "\r\n" +
                "DECLARE @CIRCUITO AS VARCHAR(1)\r\n" +
                "DECLARE @COMPROBANTE AS VARCHAR(5)\r\n" +
                "SET @CIRCUITO ='V'\r\n" +
                "SET @COMPROBANTE='DU'\r\n" +
                "\r\n" +
                "SELECT *\r\n" +
                "FROM        RelDisTipo     A\r\n" +
                "INNER JOIN    DisFormu    B ON (A.ditdif_Cod=B.dif_Cod)\r\n" +
                "WHERE DIT_CIRCUITO=@CIRCUITO  --AND DIT_TIPOFIJO=@COMPROBANTE",
                "Ajustá @CIRCUITO / @COMPROBANTE si hace falta. La base se completa con la elegida arriba.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Listado de RPT en «{base}»: {filasDevueltas} fila(s)."),

            new Item(
                "Buscar tipo comprobante (Compras)",
                "use SBDA<CODIGO>\r\n" +
                "select * from cabcompra where ccotco_cod='INGRESAR CODIGO'\r\n" +
                "select * from segtiposc where spctco_cod='INGRESAR CODIGO'",
                "Reemplazá INGRESAR CODIGO (ej. 'di') por el código de comprobante a buscar antes de ejecutar.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Búsqueda de tipo de comprobante en «{base}». Filas encontradas: {filasDevueltas} (revisá las pestañas de resultado)."),

            new Item(
                "Desactivar autoguardado FORMANAGER",
                "USE [<baseDeDatos>]\r\n" +
                "UPDATE Talonar\r\n" +
                "SET tal_Autoguardado='0' WHERE tal_Cod='INGRESAR CODIGO'",
                "Reemplazá INGRESAR CODIGO por el código del talonario con el problema.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Autoguardado desactivado en «{base}». Filas modificadas: {filas}."),

            new Item(
                "Compartir monedas e índices",
                "USE [<baseDeDatos>]\r\n" +
                "GO\r\n" +
                "SET ANSI_NULLS ON\r\n" +
                "GO\r\n" +
                "SET QUOTED_IDENTIFIER ON\r\n" +
                "GO\r\n" +
                "DROP TABLE [dbo].[ind]\r\n" +
                "GO\r\n" +
                "DROP TABLE [dbo].[ind_val]\r\n" +
                "GO\r\n" +
                "DROP TABLE [dbo].[mon]\r\n" +
                "GO\r\n" +
                "DROP TABLE [dbo].[mon_cam]\r\n" +
                "GO\r\n" +
                "DROP TABLE [dbo].[mon_tca]\r\n" +
                "GO\r\n" +
                "CREATE VIEW [dbo].[ind] AS SELECT * From manager.dbo.ind\r\n" +
                "GO\r\n" +
                "CREATE VIEW [dbo].[ind_val] AS SELECT * From manager.dbo.ind_val\r\n" +
                "GO\r\n" +
                "CREATE VIEW [dbo].[mon] AS SELECT * From manager.dbo.mon\r\n" +
                "GO\r\n" +
                "CREATE VIEW [dbo].[mon_cam] AS SELECT * From manager.dbo.mon_cam\r\n" +
                "GO\r\n" +
                "CREATE VIEW [dbo].[mon_tca] AS SELECT * From manager.dbo.mon_tca\r\n" +
                "GO",
                "En la base elegida: borra las tablas locales ind/mon… y las reemplaza por vistas a manager. Hacé backup antes.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Vistas de monedas e índices creadas en «{base}» apuntando a manager."),

            new Item(
                "Listar legajos (doc / CUIL / CBU)",
                "USE [<baseDeDatos>]\r\n" +
                "select leg_numdoc, leg_cuil, concat(leg_cbu1, leg_cbu2, leg_CBU) as acumCBU\r\n" +
                "from dbo.leg_2\r\n" +
                "where leg_2_regactual='1'",
                "Normalmente se usa sobre SJGUIA (sueldos). Elegí esa base arriba antes de ejecutar.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Legajos en «{base}»: {filasDevueltas} fila(s)."),
        ]),

        new Group("Empresa / Manager",
        [
            new Item(
                "Cambiar razón social de empresa",
                "update emp set emp_razsoc = 'Nombre Razon social' where emp_codigo = 'codigo empresa'",
                "Editá el nombre y el código de empresa antes de ejecutar. Suele ir sobre MANAGER.",
                SuccessMessage: "Razón social actualizada. Filas modificadas: {filas}."),

            new Item(
                "Eliminar ejercicio contable de parámetros",
                "use manager\r\n" +
                "update paramgen set pgeeje_emp_codigo=null\r\n" +
                "update paramgen set pgeeje_nroeje='0'",
                "Siempre se ejecuta sobre MANAGER. Limpia el ejercicio contable en paramgen (empresa y número de ejercicio).",
                UsesSelectedDatabase: false,
                SuccessMessage: "Ejercicio contable eliminado de parámetros. Filas modificadas: {filas}.",
                UsesManagerDatabase: true),

            new Item(
                "Quitar aviso de backup pendiente",
                "use manager\r\n" +
                "insert into LogBackUps values ('ADM','',null, getdate(),'No se ha realizado Backup durante las últimas 24 horas de la Base de Datos del Administrador General. Se recomienda su realización y la revisión del plan de mantenimiento de la BD.','','S')",
                "Siempre se ejecuta sobre MANAGER. Registra el backup como realizado para que Bejerman no muestre el cartel al iniciar.",
                UsesSelectedDatabase: false,
                SuccessMessage: "Aviso de backup pendiente desactivado en MANAGER. Filas insertadas: {filas}.",
                UsesManagerDatabase: true),

            new Item(
                "Quitar aviso de plan de mantenimiento pendiente",
                "use manager\r\n" +
                "insert into LogPlanesMant values ('ADM','',NULL,getdate(),'No se ha ejecutado un Plan de Mantenimiento en los últimos 31 días para la Base de Datos del Administrador General. Se recomienda su realización verificando la integridad de la base de datos.','','S')",
                "Siempre se ejecuta sobre MANAGER. Registra el plan de mantenimiento como realizado para que Bejerman no muestre el cartel al iniciar.",
                UsesSelectedDatabase: false,
                SuccessMessage: "Aviso de plan de mantenimiento pendiente desactivado en MANAGER. Filas insertadas: {filas}.",
                UsesManagerDatabase: true),
        ]),

        new Group("Triggers",
        [
            new Item(
                "Ver triggers activos e inactivos",
                "-- CONSULTAR TRIGGERS Y SU ESTADO\r\n" +
                "-- Lista cada trigger de usuario con su tabla y si está habilitado o no.\r\n" +
                "-- Columna IsDisabled => 0 = HABILITADO, 1 = DESHABILITADO.\r\n" +
                "SELECT\r\n" +
                "    t.name AS TriggerName,\r\n" +
                "    OBJECT_NAME(t.parent_id) AS TableName,\r\n" +
                "    s.name AS SchemaName,\r\n" +
                "    t.is_disabled AS IsDisabled,\r\n" +
                "    t.create_date,\r\n" +
                "    t.modify_date\r\n" +
                "FROM sys.triggers t\r\n" +
                "INNER JOIN sys.tables tbl ON t.parent_id = tbl.object_id\r\n" +
                "INNER JOIN sys.schemas s ON tbl.schema_id = s.schema_id\r\n" +
                "WHERE t.is_ms_shipped = 0\r\n" +
                "ORDER BY TableName, TriggerName;",
                SuccessMessage: "Listado de triggers: {filasDevueltas} fila(s). IsDisabled = 0 habilitado, 1 deshabilitado."),

            new Item(
                "Resumen de tablas con triggers",
                "-- TABLAS CON TRIGGERS EN LA BASE\r\n" +
                "-- Resumen por tabla: esquema, nombre y cantidad de triggers (habilitados / deshabilitados).\r\n" +
                "SELECT\r\n" +
                "    s.name AS SchemaName,\r\n" +
                "    t.name AS TableName,\r\n" +
                "    COUNT(tr.object_id) AS TotalTriggers,\r\n" +
                "    SUM(CASE WHEN tr.is_disabled = 0 THEN 1 ELSE 0 END) AS Habilitados,\r\n" +
                "    SUM(CASE WHEN tr.is_disabled = 1 THEN 1 ELSE 0 END) AS Deshabilitados\r\n" +
                "FROM sys.triggers tr\r\n" +
                "INNER JOIN sys.tables t ON tr.parent_id = t.object_id\r\n" +
                "INNER JOIN sys.schemas s ON t.schema_id = s.schema_id\r\n" +
                "WHERE tr.is_ms_shipped = 0\r\n" +
                "GROUP BY s.name, t.name\r\n" +
                "ORDER BY TableName;",
                SuccessMessage: "Resumen: {filasDevueltas} tabla(s) con triggers."),

            new Item(
                "Deshabilitar todos los triggers",
                "/* DESHABILITAR -------------------------------------------------------------\r\n" +
                "   El resultado son las sentencias para DESHABILITAR todos los triggers,\r\n" +
                "   sin necesidad de borrarlos. Copiá las columnas del resultado (a + TABLA + b),\r\n" +
                "   pegalas en una nueva consulta y ejecutalas. */\r\n" +
                "select\r\n" +
                "'ALTER TABLE ' as a,\r\n" +
                "TABLA = LTRIM(RTRIM(SO.name)),\r\n" +
                "'DISABLE TRIGGER ALL ' as b\r\n" +
                "from sysobjects as SO\r\n" +
                "left join sysobjects as SOdel on SO.deltrig = SOdel.id\r\n" +
                "left join sysobjects as SOins on SO.instrig = SOins.id\r\n" +
                "left join sysobjects as SOupd on SO.updtrig = SOupd.id\r\n" +
                "left join sysobjects as SOsel on SO.seltrig = SOsel.id\r\n" +
                "where (SO.deltrig <> 0 or SO.instrig <> 0 or SO.updtrig <> 0 or SO.seltrig <> 0)\r\n" +
                "and SO.xtype <> 'TR';",
                "Genera las sentencias DISABLE TRIGGER ALL para copiar y ejecutar.",
                SuccessMessage: "Se generaron {filasDevueltas} sentencia(s) para DESHABILITAR triggers. Copiá las columnas (a + TABLA + b), pegá en una nueva consulta y ejecutá."),

            new Item(
                "Habilitar todos los triggers",
                "/* HABILITAR ----------------------------------------------------------------\r\n" +
                "   El resultado son las sentencias para HABILITAR todos los triggers\r\n" +
                "   que se habían deshabilitado. Copiá las columnas del resultado (a + TABLA + b),\r\n" +
                "   pegalas en una nueva consulta y ejecutalas. */\r\n" +
                "select\r\n" +
                "'ALTER TABLE ' as a,\r\n" +
                "TABLA = LTRIM(RTRIM(SO.name)),\r\n" +
                "'ENABLE TRIGGER ALL ' as b\r\n" +
                "from sysobjects as SO\r\n" +
                "left join sysobjects as SOdel on SO.deltrig = SOdel.id\r\n" +
                "left join sysobjects as SOins on SO.instrig = SOins.id\r\n" +
                "left join sysobjects as SOupd on SO.updtrig = SOupd.id\r\n" +
                "left join sysobjects as SOsel on SO.seltrig = SOsel.id\r\n" +
                "where (SO.deltrig <> 0 or SO.instrig <> 0 or SO.updtrig <> 0 or SO.seltrig <> 0)\r\n" +
                "and SO.xtype <> 'TR';",
                "Genera las sentencias ENABLE TRIGGER ALL para copiar y ejecutar.",
                SuccessMessage: "Se generaron {filasDevueltas} sentencia(s) para HABILITAR triggers. Copiá las columnas (a + TABLA + b), pegá en una nueva consulta y ejecutá."),
        ]),

        new Group("Padrónes",
        [
            new Item(
                "Vaciar padrón CABA",
                "use SBDA<CODIGO>\r\n" +
                "delete from IBcabaPadron\r\n" +
                "go\r\n" +
                "delete from IBCABAPadronRegDif",
                "Borra el padrón de Ingresos Brutos CABA en la base elegida.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Padrón CABA vaciado en «{base}». Filas borradas: {filas}."),

            new Item(
                "Vaciar padrón Buenos Aires",
                "use SBDA<CODIGO>\r\n" +
                "delete from IBBAPadron\r\n" +
                "go\r\n" +
                "delete from IBBAPadronAux",
                "Borra el padrón de Ingresos Brutos BA en la base elegida.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Padrón Buenos Aires vaciado en «{base}». Filas borradas: {filas}."),

            new Item(
                "Vaciar padrón Tucumán",
                "use SBDA<CODIGO>\r\n" +
                "delete from PadronIBTucuman\r\n" +
                "go\r\n" +
                "delete from PadronIBTucumanAux",
                "Borra el padrón de Ingresos Brutos Tucumán en la base elegida.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Padrón Tucumán vaciado en «{base}». Filas borradas: {filas}."),
        ]),

        new Group("Usuarios",
        [
            new Item(
                "Ver usuarios conectados",
                "USE [<baseDeDatos>]\r\n" +
                "SELECT\r\n" +
                "    ter_WinPc       AS PC,\r\n" +
                "    ter_WinUsuario  AS UsuarioWindows,\r\n" +
                "    terusu_codigo   AS CodigoUsuario,\r\n" +
                "    ter_logintime   AS HoraLogin,\r\n" +
                "    ter_DatAdic     AS DatosAdicionales,\r\n" +
                "    ssis_descrip    AS Sesion,\r\n" +
                "    emp_codigo      AS Empresa,\r\n" +
                "    eje_nroeje      AS Ejercicio,\r\n" +
                "    bas.ssis_codigo AS CodigoSesion\r\n" +
                "FROM ssis\r\n" +
                "RIGHT JOIN (bas RIGHT JOIN Terminales ON bas.bas_codigo = Terminales.terbas_codigo)\r\n" +
                "    ON ssis.ssis_codigo = bas.ssis_codigo\r\n" +
                "ORDER BY terusu_codigo, ter_WinPc",
                "Siempre se ejecuta sobre MANAGER (no usa la base de empresa elegida). Es la misma consulta " +
                "que usa el propio Bejerman (MenuRDO) para mostrar quién está conectado: PC, usuario de " +
                "Windows, empresa, ejercicio y sesión.",
                UsesSelectedDatabase: false,
                SuccessMessage: "Usuarios/terminales conectados en «{base}»: {filasDevueltas} fila(s).",
                UsesManagerDatabase: true),

            new Item(
                "Desconectar a todos los usuarios",
                "USE [<baseDeDatos>]\r\n" +
                "SELECT\r\n" +
                "    ter_WinPc       AS PC,\r\n" +
                "    ter_WinUsuario  AS UsuarioWindows,\r\n" +
                "    terusu_codigo   AS CodigoUsuario,\r\n" +
                "    ter_logintime   AS HoraLogin,\r\n" +
                "    ter_DatAdic     AS DatosAdicionales,\r\n" +
                "    ssis_descrip    AS Sesion,\r\n" +
                "    emp_codigo      AS Empresa,\r\n" +
                "    eje_nroeje      AS Ejercicio,\r\n" +
                "    bas.ssis_codigo AS CodigoSesion\r\n" +
                "FROM ssis\r\n" +
                "RIGHT JOIN (bas RIGHT JOIN Terminales ON bas.bas_codigo = Terminales.terbas_codigo)\r\n" +
                "    ON ssis.ssis_codigo = bas.ssis_codigo\r\n" +
                "ORDER BY terusu_codigo, ter_WinPc\r\n" +
                "delete from procxter\r\n" +
                "delete from terminales",
                "Siempre se ejecuta sobre MANAGER. Borra procxter y terminales. Antes de borrar te muestra " +
                "el mismo listado detallado (PC, usuario, empresa, ejercicio) que usa el propio Bejerman.",
                UsesSelectedDatabase: false,
                SuccessMessage: "Usuarios desconectados en «{base}». Filas borradas: {filas}. Abajo ves el listado previo de terminales ({filasDevueltas} fila(s)).",
                UsesManagerDatabase: true),

            new Item(
                "Crear usuario BEJERMAN",
                "sp_addlogin @loginame = 'BEJERMAN',@passwd = '<CLAVE_SQL>',@defdb = 'master', @deflanguage = 'us_english'\r\n" +
                "go\r\n" +
                "sp_grantdbaccess @loginame = 'BEJERMAN'\r\n" +
                "go\r\n" +
                "sp_addrolemember  @rolename = 'db_owner' , @membername = 'BEJERMAN'\r\n" +
                "go\r\n" +
                "sp_addsrvrolemember @rolename = 'sysadmin',  @loginame = 'BEJERMAN'\r\n" +
                "go",
                "Reemplazá <CLAVE_SQL> por la clave local antes de ejecutar. Crea el login BEJERMAN, lo agrega a la base elegida como db_owner y le da sysadmin.",
                SuccessMessage: "Usuario BEJERMAN creado/configurado correctamente (login, acceso a la base y sysadmin)."),

            new Item(
                "Error al cambiar de usuario (perfil)",
                "use manager\r\n" +
                "alter table RelEmpUsuEFW add reu_PerfilUsuario varchar(5) null",
                "Siempre se ejecuta sobre MANAGER. Agrega la columna reu_PerfilUsuario si falta (error al cambiar de usuario).",
                UsesSelectedDatabase: false,
                SuccessMessage: "Columna reu_PerfilUsuario agregada (o ya existía). Revisá el mensaje del servidor.",
                UsesManagerDatabase: true),

            new Item(
                "Reemplazar clave de usuario",
                "use manager\r\n" +
                "update usu set usu_clave=''  where usu_codigo='INGRESAR CODIGO'",
                "Siempre se ejecuta sobre MANAGER. Reemplazá INGRESAR CODIGO por el código real del usuario. Deja la clave vacía.",
                UsesSelectedDatabase: false,
                SuccessMessage: "Clave de usuario actualizada en MANAGER. Filas modificadas: {filas}.",
                UsesManagerDatabase: true),
        ]),

        new Group("Mantenimiento de base",
        [
            new Item(
                "Desactivar xp_cmdshell",
                "Use Master\r\n" +
                "GO\r\n" +
                "EXEC master.dbo.sp_configure 'xp_cmdshell', 0\r\n" +
                "RECONFIGURE WITH OVERRIDE\r\n" +
                "GO\r\n" +
                "EXEC master.dbo.sp_configure 'show advanced options', 0\r\n" +
                "RECONFIGURE WITH OVERRIDE\r\n" +
                "GO",
                "Desactiva xp_cmdshell y las opciones avanzadas en el servidor (master).",
                SuccessMessage: "xp_cmdshell desactivado en el servidor."),

            new Item(
                "Reducir logs (todas las bases)",
                "DECLARE @sDbName VARCHAR(40)\r\n" +
                "DECLARE @sDbRecovery VARCHAR(40)\r\n" +
                "DECLARE @sLogName VARCHAR(40)\r\n" +
                "DECLARE @sDBIsSimple_Shrink VARCHAR(4000)\r\n" +
                "DECLARE @sSetRecoverySimple VARCHAR(4000)\r\n" +
                "DECLARE @sShrinkDBLog VARCHAR(4000)\r\n" +
                "DECLARE @sReSetRecovery VARCHAR(4000)\r\n" +
                "DECLARE @sGetLogName VARCHAR(4000)\r\n" +
                "DECLARE @sqlCommand VARCHAR(MAX)\r\n" +
                "\r\n" +
                "-- Cursor para recorrer todas las bases de datos del servidor (excepto las del sistema)\r\n" +
                "DECLARE db_cursor CURSOR FOR\r\n" +
                "SELECT name\r\n" +
                "FROM sys.databases\r\n" +
                "WHERE name NOT IN ('master', 'tempdb', 'model', 'msdb')\r\n" +
                "\r\n" +
                "OPEN db_cursor\r\n" +
                "FETCH NEXT FROM db_cursor INTO @sDbName\r\n" +
                "\r\n" +
                "WHILE @@FETCH_STATUS = 0\r\n" +
                "BEGIN\r\n" +
                "-- Obtener el modelo de recuperación de la base de datos\r\n" +
                "SET @sDbRecovery = CAST(DATABASEPROPERTYEX(@sDbName, 'Recovery') AS VARCHAR(40))\r\n" +
                "\r\n" +
                "-- Obtener el nombre del archivo de log\r\n" +
                "SET @sGetLogName = ('USE ' + @sDbName + ';\r\n" +
                "SELECT name\r\n" +
                "FROM sys.database_files\r\n" +
                "WHERE type = 1')\r\n" +
                "\r\n" +
                "-- Crear tabla temporal para obtener el nombre del archivo de log\r\n" +
                "IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ResultSet]') AND type IN (N'U'))\r\n" +
                "DROP TABLE [dbo].[ResultSet]\r\n" +
                "\r\n" +
                "-- Crear la tabla para almacenar el nombre del log\r\n" +
                "CREATE TABLE ResultSet (SetLogName VARCHAR(400))\r\n" +
                "-- Ejecutar el comando que obtiene el nombre del log\r\n" +
                "INSERT INTO ResultSet EXEC(@sGetLogName)\r\n" +
                "SET @sLogName = (SELECT SetLogName FROM ResultSet)\r\n" +
                "\r\n" +
                "-- Eliminar la tabla temporal\r\n" +
                "DROP TABLE ResultSet\r\n" +
                "\r\n" +
                "-- Ahora ejecutamos el código de reducción de logs según el modelo de recuperación\r\n" +
                "IF @sDbRecovery = 'Simple'\r\n" +
                "BEGIN\r\n" +
                "SET @sDBIsSimple_Shrink = 'USE ' + @sDbName + ';\r\n" +
                "DBCC SHRINKFILE (' + @sLogName + ', 1);'\r\n" +
                "EXEC(@sDBIsSimple_Shrink)\r\n" +
                "END\r\n" +
                "ELSE IF @sDbRecovery = 'BULK_LOGGED'\r\n" +
                "BEGIN\r\n" +
                "-- Cambiar a modelo SIMPLE, reducir el log y luego restablecer el modelo BULK_LOGGED\r\n" +
                "SET @sSetRecoverySimple = 'ALTER DATABASE ' + @sDbName + '\r\n" +
                "SET RECOVERY SIMPLE'\r\n" +
                "EXEC (@sSetRecoverySimple)\r\n" +
                "\r\n" +
                "SET @sShrinkDBLog = 'USE ' + @sDbName + ';\r\n" +
                "DBCC SHRINKFILE (' + @sLogName + ', 1);'\r\n" +
                "EXEC(@sShrinkDBLog)\r\n" +
                "\r\n" +
                "SET @sReSetRecovery = 'ALTER DATABASE ' + @sDbName + '\r\n" +
                "SET RECOVERY BULK_LOGGED'\r\n" +
                "EXEC (@sReSetRecovery)\r\n" +
                "END\r\n" +
                "ELSE\r\n" +
                "BEGIN\r\n" +
                "-- Cambiar a modelo SIMPLE, reducir el log y luego restablecer el modelo FULL\r\n" +
                "SET @sSetRecoverySimple = 'ALTER DATABASE ' + @sDbName + '\r\n" +
                "SET RECOVERY SIMPLE'\r\n" +
                "EXEC (@sSetRecoverySimple)\r\n" +
                "\r\n" +
                "SET @sShrinkDBLog = 'USE ' + @sDbName + ';\r\n" +
                "DBCC SHRINKFILE (' + @sLogName + ', 1);'\r\n" +
                "EXEC(@sShrinkDBLog)\r\n" +
                "\r\n" +
                "SET @sReSetRecovery = 'ALTER DATABASE ' + @sDbName + '\r\n" +
                "SET RECOVERY FULL'\r\n" +
                "EXEC (@sReSetRecovery)\r\n" +
                "END\r\n" +
                "\r\n" +
                "-- Obtener la siguiente base de datos\r\n" +
                "FETCH NEXT FROM db_cursor INTO @sDbName\r\n" +
                "END\r\n" +
                "\r\n" +
                "-- Cerrar y liberar el cursor\r\n" +
                "CLOSE db_cursor\r\n" +
                "DEALLOCATE db_cursor",
                "Recorre todas las bases del servidor y achica los archivos de log.",
                SuccessMessage: "Reducción de logs finalizada en todas las bases del servidor (excepto sistema)."),

            new Item(
                "Verificar integridad (CHECKDB)",
                "-- El nombre de la base se completa con la elegida arriba (cambia sola si cambiás de base).\r\n" +
                "DBCC CHECKDB ('<baseDeDatos>') WITH NO_INFOMSGS, ALL_ERRORMSGS;",
                "Revisa si la base tiene errores de integridad. No repara nada.",
                UsesSelectedDatabase: true,
                SuccessMessage: "CHECKDB finalizado en «{base}». Si no hubo error, la integridad está OK."),

            new Item(
                "Reparar base dañada",
                "-- ATENCIÓN: REPAIR_ALLOW_DATA_LOSS puede provocar PÉRDIDA DE DATOS. Hacé un backup antes.\r\n" +
                "-- Requiere dejar la base en modo usuario único. El nombre se completa con la base elegida arriba.\r\n" +
                "ALTER DATABASE [<baseDeDatos>] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\r\n" +
                "GO\r\n" +
                "DBCC CHECKDB ('<baseDeDatos>', REPAIR_ALLOW_DATA_LOSS) WITH ALL_ERRORMSGS;\r\n" +
                "GO\r\n" +
                "ALTER DATABASE [<baseDeDatos>] SET MULTI_USER;",
                "Intenta reparar la base elegida.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Reparación de «{base}» finalizada. Revisá mensajes del servidor por si quedó algún error."),

            new Item(
                "Recuperar base de Recovery",
                "RESTORE DATABASE [<baseDeDatos>] WITH RECOVERY\r\n" +
                "GO",
                "Deja online una base que quedó en RESTORING / Recovery.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Base «{base}» recuperada (WITH RECOVERY). Debería quedar online."),

            new Item(
                "Recuperar base Suspect",
                "ALTER DATABASE [<baseDeDatos>] SET EMERGENCY\r\n" +
                "GO\r\n" +
                "ALTER DATABASE [<baseDeDatos>] SET SINGLE_USER\r\n" +
                "GO\r\n" +
                "DBCC CHECKDB ([<baseDeDatos>], REPAIR_ALLOW_DATA_LOSS)\r\n" +
                "GO\r\n" +
                "ALTER DATABASE [<baseDeDatos>] SET MULTI_USER\r\n" +
                "GO",
                "Usalo sólo si la base está SUSPECT.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Proceso de recuperación SUSPECT finalizado en «{base}». Verificá que la base haya quedado online."),

            new Item(
                "Recuperar base Recovery Pending",
                "ALTER DATABASE [<baseDeDatos>] SET EMERGENCY;\r\n" +
                "GO\r\n" +
                "ALTER DATABASE [<baseDeDatos>] SET SINGLE_USER\r\n" +
                "GO\r\n" +
                "DBCC CHECKDB ([<baseDeDatos>], REPAIR_ALLOW_DATA_LOSS) WITH ALL_ERRORMSGS;\r\n" +
                "GO\r\n" +
                "ALTER DATABASE [<baseDeDatos>] SET MULTI_USER\r\n" +
                "GO",
                "Usalo sólo si la base está RECOVERY_PENDING.",
                UsesSelectedDatabase: true,
                SuccessMessage: "Proceso de recuperación RECOVERY_PENDING finalizado en «{base}». Verificá que la base haya quedado online."),

            new Item(
                "Crear base SBSJ desde SJGUIA",
                "IF (EXISTS (SELECT name\r\n" +
                "FROM master.dbo.sysdatabases\r\n" +
                "WHERE ('[' + name + ']' = 'sbsj'\r\n" +
                "OR name = 'sbsj')))\r\n" +
                "\r\n" +
                "drop database sbsj\r\n" +
                "\r\n" +
                "Declare @ruta as nvarchar(400)\r\n" +
                "Declare @Consulta as nvarchar(400)\r\n" +
                "Declare @Consulta2 as nvarchar(400)\r\n" +
                "\r\n" +
                "SELECT @ruta=(physical_name)\r\n" +
                "from sys.master_files where name='sjguia_dat'\r\n" +
                "\r\n" +
                "print @ruta\r\n" +
                "\r\n" +
                "select replace (@ruta,'SJGUIA\\SJGUIA_dat.MDF','')+'SBSJ.bak'\r\n" +
                "\r\n" +
                "\r\n" +
                "SET @Consulta=' BACKUP DATABASE sjguia TO DISK = '''+ replace (@ruta,'SJGUIA\\SJGUIA_dat.MDF','')+'SBSJ.bak'+ ''' WITH INIT'\r\n" +
                "\r\n" +
                "\r\n" +
                "EXECUTE sp_executesql @Consulta\r\n" +
                "\r\n" +
                "SET @Consulta2= '\r\n" +
                "RESTORE DATABASE\r\n" +
                "SBSJ\r\n" +
                "FROM\r\n" +
                "DISK='''+replace (@ruta,'SJGUIA\\SJGUIA_dat.MDF','')+'SBSJ'+'.bak''\r\n" +
                "\r\n" +
                "\r\n" +
                "WITH\r\n" +
                "MOVE ''SJGUIA_DAT'' TO '''+ replace (@ruta,'SJGUIA\\SJGUIA_dat.MDF','')+'SBSJ_DAT'+'.mdf'',\r\n" +
                "MOVE ''SJGUIA_LOG'' TO '''+ replace (@ruta,'SJGUIA\\SJGUIA_dat.MDF','')+'SBSJ_LOG'+'.ldf''\r\n" +
                "'\r\n" +
                "\r\n" +
                "EXECUTE sp_executesql @Consulta2\r\n" +
                "\r\n" +
                "go",
                "Requiere que exista sjguia. Si ya hay SBSJ, la borra y la recrea.",
                SuccessMessage: "Base SBSJ creada/recreada desde SJGUIA. Si hay filas abajo, son datos informativos del proceso."),
        ]),
    ];
}
