namespace SBBackup.Models;

public sealed class ServerDatabaseRow
{
    public bool Selected { get; set; }
    public string PhysicalName { get; init; } = "";
    public string FriendlyName { get; set; } = "";
    /// <summary>Código de empresa en manager.EMP que coincide con el nombre de la base (si aplica).</summary>
    public string? MatchedEmpCode { get; set; }
    /// <summary>Etiqueta interna (Gestión, SJ, Ejercicio Contable).</summary>
    public string ProductLine { get; set; } = "";
    /// <summary>True si la fila se agregó como ejercicio vinculado por prefijo SBDA+4 dígitos.</summary>
    public bool IsSbdaLinkedExercise { get; set; }
    /// <summary>Texto mostrado en la columna Tipo de la grilla.</summary>
    public string GridTipo { get; set; } = "";
    /// <summary>Texto mostrado en la columna Nombre de la grilla.</summary>
    public string GridNombre { get; set; } = "";
    /// <summary>Nº de ejercicio para ordenar filas de contabilidad (1, 2, 3…).</summary>
    public int? ExerciseSortOrder { get; set; }
}
