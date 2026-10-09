using System.Text.Json;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

internal static class BuildingTemplateEditSnapshot
{
    internal static string Capture(
        IEnumerable<BuildingTemplate> templates,
        BuildingTemplate? selectedTemplate,
        IReadOnlyList<BuildingTemplateRow> selectedRows)
        => JsonSerializer.Serialize(templates.Select(template => new
        {
            template.Id,
            template.Name,
            template.CreatedByTribe,
            Rows = ReferenceEquals(template, selectedTemplate) ? selectedRows : template.Rows,
        }));
}
