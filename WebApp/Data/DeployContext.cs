using System.Text.Json;
using DeployManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DeployManager.Data;

public class DeployContext : DbContext
{
    public DbSet<DeploymentJob>   Jobs     { get; set; }
    public DbSet<Machine>         Machines { get; set; }
    public DbSet<SoftwarePackage> Packages { get; set; }
    public DbSet<SoftwareItem>    Software { get; set; }
    public DbSet<WimImage>        Wims     { get; set; }
    public DbSet<DriverPackage>   Drivers  { get; set; }
    public DbSet<AppUser>         Users    { get; set; }

    public DeployContext(DbContextOptions<DeployContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder model)
    {
        static ValueConverter<List<T>, string> JsonConv<T>() =>
            new(v  => JsonSerializer.Serialize(v,  (JsonSerializerOptions?)null),
                v  => JsonSerializer.Deserialize<List<T>>(v, (JsonSerializerOptions?)null) ?? new());

        static ValueComparer<List<T>> ListComparer<T>() =>
            new(
                (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null)
                       == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                c  => JsonSerializer.Serialize(c, (JsonSerializerOptions?)null).GetHashCode(),
                c  => JsonSerializer.Deserialize<List<T>>(
                          JsonSerializer.Serialize(c, (JsonSerializerOptions?)null),
                          (JsonSerializerOptions?)null) ?? new());

        model.Entity<DeploymentJob>(e =>
        {
            e.HasIndex(j => j.MacAddress);
            e.HasIndex(j => j.Created);
            e.HasIndex(j => j.Status);
            e.Property(j => j.Log)
             .HasConversion(JsonConv<string>())
             .Metadata.SetValueComparer(ListComparer<string>());
            e.Property(j => j.SoftwareResults)
             .HasConversion(JsonConv<SoftwareInstallResult>())
             .Metadata.SetValueComparer(ListComparer<SoftwareInstallResult>());
        });

        model.Entity<SoftwarePackage>(e =>
        {
            e.Property(p => p.SoftwareIds)
             .HasConversion(JsonConv<string>())
             .Metadata.SetValueComparer(ListComparer<string>());
        });

        model.Entity<Machine>(e =>
        {
            e.HasIndex(m => m.MacAddress);
        });

        model.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.Upn);
        });
    }
}
