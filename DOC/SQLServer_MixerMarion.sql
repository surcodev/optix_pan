/* ============================================================================
   HMI Mezcladora Marion - Tablas para FactoryTalk Optix en SQL Server
   Base de datos: optix

   - Se puede ejecutar varias veces: crea solo lo que falta.
   - No borra ni modifica tablas o recetas que ya existan.
   - No cambie los nombres de tablas ni de columnas: Optix los busca tal cual.
   - Las columnas que empiezan con "/" son los parámetros de la receta.
   ============================================================================ */
USE optix;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------------------------------------------------------------------------
   1. Recetas
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.SchemaBatidora', N'U') IS NULL
CREATE TABLE dbo.SchemaBatidora
(
    [Name]                    nvarchar(100) NOT NULL CONSTRAINT PK_SchemaBatidora PRIMARY KEY,
    [/NombreReceta]           nvarchar(200) NULL,
    [/Carga_HabilitarM2]      bit  NULL,
    [/Carga_TiempoSeg]        int  NULL,
    [/Carga_ModoOperador]     bit  NULL,
    [/Mezcla_TiempoTotalMin]  int  NULL,
    [/NoTam_Categoria]        int  NULL,   -- 0 Nada, 1 Manteca, 2 Semillas, 3 Aceite, 4 Galletas
    [/NoTam_MinInicio]        real NULL,
    [/NoTam_MinConfirmacion]  real NULL,   -- minutos decimales: 7:55 = 7.916667
    [/NoTam_MinFinCronometro] real NULL,   -- 0 = no pausa el cronómetro
    [/Chopper_MinEncendido]   real NULL,
    [/Chopper_MinutosUso]     real NULL,
    [/Descarga_TiempoMinMin]  real NULL
);
GO

/* ---------------------------------------------------------------------------
   2. Tendencias: una fila por segundo
      Timestamp = hora UTC, LocalTimestamp = hora local.
      T_* son las señales desplazadas que usa el gráfico "Equipos y sensores".
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.RegistroProceso', N'U') IS NULL
CREATE TABLE dbo.RegistroProceso
(
    [Timestamp]      datetime NULL,
    [LocalTimestamp] datetime NULL,
    [M1]      bit  NULL,
    [M2]      bit  NULL,
    [SC1]     bit  NULL,
    [V1]      bit  NULL,
    [SI1]     bit  NULL,
    [CronoM2] real NULL,
    [CronoM3] real NULL,
    [CronoM4] real NULL,
    [T_M1]    real NULL,
    [T_M2]    real NULL,
    [T_SC1]   real NULL,
    [T_V1]    real NULL,
    [T_SI1]   real NULL,
    [Id]      bigint NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.RegistroProceso') AND name = N'Timestamp_Index')
    CREATE INDEX Timestamp_Index ON dbo.RegistroProceso ([Timestamp]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.RegistroProceso') AND name = N'LocalTimestamp_Index')
    CREATE INDEX LocalTimestamp_Index ON dbo.RegistroProceso ([LocalTimestamp]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.RegistroProceso') AND name = N'Id_Index')
    CREATE INDEX Id_Index ON dbo.RegistroProceso ([Id]);
GO

/* ---------------------------------------------------------------------------
   3. Historial de alarmas
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.RegistroAlarmas', N'U') IS NULL
CREATE TABLE dbo.RegistroAlarmas
(
    [ActiveState_Id]    bit NULL,
    [AckedState_Id]     bit NULL,
    [ConfirmedState_Id] bit NULL,
    [ConditionName]     nvarchar(200) NULL,
    [EnabledState_Id]   bit NULL,
    [SourceName]        nvarchar(200) NULL,
    [Time]              datetime NULL,
    [Message_en-US]     nvarchar(500) NULL,
    [Severity]          int NULL,
    [LocalTime]         datetime NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.RegistroAlarmas') AND name = N'IX_RegistroAlarmas_LocalTime')
    CREATE INDEX IX_RegistroAlarmas_LocalTime ON dbo.RegistroAlarmas ([LocalTime]);
GO

/* ---------------------------------------------------------------------------
   4. Historial de acciones sobre recetas
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.HistorialRecetas', N'U') IS NULL
CREATE TABLE dbo.HistorialRecetas
(
    [Timestamp] datetime NULL,
    [Receta]    nvarchar(100) NULL,
    [Accion]    nvarchar(50)  NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.HistorialRecetas') AND name = N'IX_HistorialRecetas_Timestamp')
    CREATE INDEX IX_HistorialRecetas_Timestamp ON dbo.HistorialRecetas ([Timestamp]);
GO

/* ---------------------------------------------------------------------------
   5. Las 5 recetas del proyecto (solo inserta las que falten)
   --------------------------------------------------------------------------- */
INSERT INTO dbo.SchemaBatidora
    ([Name], [/NombreReceta], [/Carga_HabilitarM2], [/Carga_TiempoSeg], [/Carga_ModoOperador],
     [/Mezcla_TiempoTotalMin], [/NoTam_Categoria], [/NoTam_MinInicio], [/NoTam_MinConfirmacion],
     [/NoTam_MinFinCronometro], [/Chopper_MinEncendido], [/Chopper_MinutosUso], [/Descarga_TiempoMinMin])
SELECT r.*
FROM (VALUES
    (N'BPE-0-13-0-0',  N'Mezcla Estándar 13 min',                        1, 15, 0, 13, 0,  0,  0.0,                   0, 0,  0, 2),
    (N'BPE-0-13-C-4',  N'Mezcla Estándar 13 min con Chopper 4 min',      1, 15, 0, 13, 0,  0,  0.0,                   0, 4,  4, 2),
    (N'BPE-M-18-C-4',  N'Mezcla con Manteca 18 min con Chopper 4 min',   1, 15, 0, 18, 1,  4,  7.0 + 55.0 / 60.0,     0, 8,  4, 2),
    (N'BPE-A-18-C-6',  N'Mezcla con Aceite 18 min con Chopper 6 min',    1, 15, 0, 18, 3,  6,  7.0 + 55.0 / 60.0,     0, 6,  2, 2),
    (N'BPE-G-26-C-20', N'Mezcla con Galletas 26 min con Chopper 20 min', 1, 15, 0, 26, 4, 13, 13.0 + 5.0 / 60.0,     13, 0, 20, 2)
) AS r ([Name], Nombre, HabM2, TSeg, ModoOp, Mezcla, Cat, Ini, Conf, FinCr, ChDesde, ChUso, Desc_)
WHERE NOT EXISTS (SELECT 1 FROM dbo.SchemaBatidora s WHERE s.[Name] = r.[Name]);
GO

/* ---------------------------------------------------------------------------
   6. Limpieza OPCIONAL del registro de proceso (86 400 filas por día).
      Solo crea el procedimiento. No se ejecuta solo.
      Uso:  EXEC dbo.LimpiarRegistroProceso @Dias = 90;
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.LimpiarRegistroProceso', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.LimpiarRegistroProceso @Dias int = 90 AS
BEGIN
    SET NOCOUNT ON;
    IF @Dias IS NULL OR @Dias < 1
        THROW 50001, ''@Dias debe ser 1 o mayor.'', 1;
    DELETE FROM dbo.RegistroProceso WHERE [LocalTimestamp] < DATEADD(DAY, -@Dias, GETDATE());
END');
GO

/* ---------------------------------------------------------------------------
   7. Verificación
   --------------------------------------------------------------------------- */
SELECT t.name AS Tabla, SUM(p.rows) AS Filas
FROM sys.tables t
JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE t.name IN (N'SchemaBatidora', N'RegistroProceso', N'RegistroAlarmas', N'HistorialRecetas')
GROUP BY t.name
ORDER BY t.name;

SELECT [Name] AS Codigo, [/NombreReceta] AS Nombre, [/Mezcla_TiempoTotalMin] AS Mezcla, [/NoTam_MinConfirmacion] AS Confirmar
FROM dbo.SchemaBatidora
ORDER BY [Name];
GO
