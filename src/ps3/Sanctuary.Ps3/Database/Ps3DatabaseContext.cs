using Microsoft.EntityFrameworkCore;

using Sanctuary.Ps3.Database.Entities;

namespace Sanctuary.Ps3.Database;

/// <summary>
/// The server's database, laid out like Sanctuary's <c>DatabaseContext</c> (Users, Characters, Items, Profiles, ProfileItems) with the
/// keys and cascades of its SQLite configuration (Sanctuary.Database.Sqlite/Configuration). The same model is used for SQLite and MySQL.
/// </summary>
public sealed class Ps3DatabaseContext : DbContext
{
    public Ps3DatabaseContext(DbContextOptions<Ps3DatabaseContext> options) : base(options)
    {
    }

    public DbSet<DbUser> Users => Set<DbUser>();

    public DbSet<DbCharacter> Characters => Set<DbCharacter>();

    public DbSet<DbItem> Items => Set<DbItem>();

    public DbSet<DbProfile> Profiles => Set<DbProfile>();

    public DbSet<DbProfileItem> ProfileItems => Set<DbProfileItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbUser>(user =>
        {
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).ValueGeneratedOnAdd();
            user.HasIndex(u => u.Username).IsUnique();
            user.Property(u => u.Username).IsRequired().HasMaxLength(254);
            user.Property(u => u.Password).IsRequired().HasMaxLength(254);

            user.HasMany(u => u.Characters)
                .WithOne(c => c.User)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DbCharacter>(character =>
        {
            character.HasKey(c => c.Id);

            // The server hands out the ids itself (character select and the game server know a character by it).
            character.Property(c => c.Id).ValueGeneratedNever();
            character.Property(c => c.FirstName).IsRequired().HasMaxLength(64);
            character.Property(c => c.LastName).HasMaxLength(64);
            character.Property(c => c.Head).IsRequired().HasMaxLength(128);
            character.Property(c => c.Hair).IsRequired().HasMaxLength(128);
            character.Property(c => c.ModelCustomization).HasMaxLength(128);
            character.Property(c => c.FacePaint).HasMaxLength(128);
            character.Property(c => c.SkinTone).IsRequired().HasMaxLength(128);
            character.Property(c => c.Zone).HasMaxLength(128);
            character.Property(c => c.CreateValues).IsRequired();

            character.HasMany(c => c.Items)
                .WithOne(i => i.Character)
                .HasForeignKey(i => i.CharacterId)
                .OnDelete(DeleteBehavior.Cascade);

            character.HasMany(c => c.Profiles)
                .WithOne(p => p.Character)
                .HasForeignKey(p => p.CharacterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DbItem>(item =>
        {
            item.HasKey(i => new { i.Id, i.CharacterId });
            item.Property(i => i.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<DbProfile>(profile =>
        {
            profile.HasKey(p => new { p.Id, p.CharacterId });
            profile.Property(p => p.Id).ValueGeneratedNever();

            profile.HasMany(p => p.Items)
                .WithOne(i => i.Profile)
                .HasForeignKey(i => new { i.ProfileId, i.CharacterId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DbProfileItem>(worn =>
        {
            worn.ToTable("ProfileItems");
            worn.HasKey(i => new { i.CharacterId, i.ProfileId, i.Slot });
        });
    }
}
