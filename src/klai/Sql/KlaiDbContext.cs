using Microsoft.EntityFrameworkCore;
using klai.Chat.Model;
using klai.Sql.Model;

namespace klai.Data;

public class KlaiDbContext : DbContext
{
    public KlaiDbContext(DbContextOptions<KlaiDbContext> options) : base(options) { }

    public DbSet<ChatMessageEntity> ChatMessages { get; set; }

    public DbSet<VectorizedNotionItem> VectorizedNotionItems { get; set; }

    public DbSet<KnowledgeArtifact> KnowledgeArtifacts { get; set; }
}