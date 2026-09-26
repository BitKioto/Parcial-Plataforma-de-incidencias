using System.ComponentModel.DataAnnotations;

namespace PlataformaIncidencias.Models;

/// <summary>
/// Incidencia registrada en la base de datos local (SQLite / EF Core).
/// La búsqueda de texto se resuelve en Algolia, pero esta entidad es la
/// fuente de verdad para el estado y para los datos que se muestran.
/// </summary>
public class Incidencia
{
    /// <summary>Clave primaria. Coincide con el <c>objectID</c> de los registros del índice de Algolia.</summary>
    public int Id { get; set; }

    [Required]
    [MaxLength(150)]
    [Display(Name = "Estación")]
    public string NombreEstacion { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string Descripcion { get; set; } = string.Empty;

    /// <summary>Estado de la incidencia. Sólo se listan las que están en <see cref="EstadoAbierta"/>.</summary>
    [Required]
    [MaxLength(50)]
    public string Estado { get; set; } = EstadoAbierta;

    [Display(Name = "Fecha de reporte")]
    public DateTime FechaReporte { get; set; } = DateTime.UtcNow;

    /// <summary>Valor esperado del estado "Abierta".</summary>
    public const string EstadoAbierta = "Abierta";

    /// <summary>Atributos de la incidencia que se indexan y sobre los que Algolia ejecuta la búsqueda.</summary>
    public const string AtributoBusquedaEstacion = "nombreEstacion";

    public const string AtributoBusquedaDescripcion = "descripcion";
}
