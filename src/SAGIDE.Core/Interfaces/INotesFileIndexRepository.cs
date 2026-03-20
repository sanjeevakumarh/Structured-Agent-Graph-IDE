using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Tracks which Logseq note files have been indexed and when.
/// Used by <c>NotesIndexerService</c> for delta processing.
/// Abstracted so <c>SAGIDE.Memory</c> has no reference to <c>SAGIDE.Service.Persistence</c>.
/// </summary>
public interface INotesFileIndexRepository
{
    Task<Dictionary<string, NotesFileEntry>> GetAllAsync();
    Task UpsertAsync(NotesFileEntry entry);
    Task DeleteAsync(string filePath);
    Task ClearAllAsync();
    Task<NotesStats> GetStatsAsync();
}
