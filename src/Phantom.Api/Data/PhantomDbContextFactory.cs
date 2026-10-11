using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Phantom.Api.Data;

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without starting the server.</summary>
public class PhantomDbContextFactory : IDesignTimeDbContextFactory<PhantomDbContext>
{
    public PhantomDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PhantomDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
