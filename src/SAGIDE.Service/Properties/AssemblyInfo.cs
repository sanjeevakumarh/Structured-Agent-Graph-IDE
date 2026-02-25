using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SAGIDE.Service.Tests")]

// Expose the top-level Program class to WebApplicationFactory in test projects.
// This partial class declaration is intentionally empty — it just makes Program public.
