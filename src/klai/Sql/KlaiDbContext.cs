using Microsoft.EntityFrameworkCore;
using klai.Chat.Model;

namespace klai.Data;

public class KlaiDbContext : DbContext
{
    public KlaiDbContext(DbContextOptions<KlaiDbContext> options) : base(options) { }

    public DbSet<ChatMessageEntity> ChatMessages { get; set; }
}