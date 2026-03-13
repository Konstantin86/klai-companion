
namespace klai.Sql.Model;

public class KnowledgeArtifact
{
    public int Id { get; set; }
    public int TopicId { get; set; }
    public string ArtifactType { get; set; } // e.g., "LocalDocument", "GoogleSheet"
    public string Uri { get; set; } // Local path (data/files/cv.docx) OR Web URL
    public string Description { get; set; } // "Please use it as a cv"
    public DateTime AddedAt { get; set; }
}