using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UWPHook
{
    /// <summary>
    /// Synchronizes non-Steam shortcut membership with Steam's modern,
    /// cloud-backed collection store.
    /// </summary>
    public static class SteamCollectionManager
    {
        private const string CollectionKeyPrefix = "user-collections.";
        private const string ManagedCollectionPrefix = "uwphook-";

        public static string[] ParseCollectionNames(string? value)
        {
            return (value ?? String.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Updates Steam's authoritative collection JSON for one Steam user.
        /// Steam must not be running when this method is called.
        /// </summary>
        public static bool Synchronize(
            string userPath,
            IEnumerable<string> collectionNames,
            IEnumerable<int> currentShortcutAppIds,
            IEnumerable<int> previousShortcutAppIds,
            IEnumerable<string>? previousCollectionNames = null)
        {
            string? storagePath = GetActiveCloudStoragePath(userPath);
            if (storagePath == null)
            {
                Log.Warning("Steam collection storage was not found for user {UserPath}; shortcut tags were written, but modern collections could not be synchronized.", userPath);
                return false;
            }

            JArray cloudData;
            try
            {
                cloudData = JArray.Parse(File.ReadAllText(storagePath));
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"Steam collection storage could not be read: {storagePath}", exception);
            }

            string[] desiredNames = collectionNames
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var desiredIds = currentShortcutAppIds.Select(ToUnsignedAppId).Distinct().ToHashSet();
            var previousIds = previousShortcutAppIds.Select(ToUnsignedAppId).Distinct().ToHashSet();
            var oldNames = (previousCollectionNames ?? Array.Empty<string>())
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var entries = ReadCollectionEntries(cloudData);
            bool changed = false;

            // Remove UWPHook shortcut IDs from their previous collections first.
            foreach (var entry in entries.Where(entry => !entry.IsDeleted && (
                entry.Id.StartsWith(ManagedCollectionPrefix, StringComparison.OrdinalIgnoreCase)
                || oldNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))))
            {
                bool remainsDesired = desiredNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);
                HashSet<long> idsToRemove = remainsDesired
                    ? previousIds.Where(appId => !desiredIds.Contains(appId)).ToHashSet()
                    : previousIds;
                bool entryChanged = RemoveAppIds(entry.Data, idsToRemove);

                if (entryChanged
                    && !remainsDesired
                    && entry.Id.StartsWith(ManagedCollectionPrefix, StringComparison.OrdinalIgnoreCase)
                    && EnsureArray(entry.Data, "added").Count == 0)
                {
                    changed |= MarkEntryDeleted(entry);
                    continue;
                }

                changed |= SaveEntryIfChanged(entry, entryChanged || entry.IsDirty);
            }

            foreach (string collectionName in desiredIds.Count == 0 ? Array.Empty<string>() : desiredNames)
            {
                CollectionEntry? entry = entries.FirstOrDefault(candidate =>
                    !candidate.IsDeleted
                    && candidate.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    string id = CreateManagedCollectionId(collectionName);
                    entry = entries.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

                    if (entry != null && entry.IsDeleted)
                    {
                        entry.IsDirty = true;
                    }
                    else if (entry == null)
                    {
                        var data = new JObject
                        {
                            ["id"] = id,
                            ["name"] = collectionName,
                            ["added"] = new JArray(),
                            ["removed"] = new JArray(),
                        };
                        var metadata = new JObject();
                        var item = new JArray(CollectionKeyPrefix + id, metadata);
                        cloudData.Add(item);
                        entry = new CollectionEntry(id, collectionName, metadata, data);
                        entries.Add(entry);
                        entry.IsDirty = true;
                    }
                }

                bool entryChanged = AddAppIds(entry.Data, desiredIds);
                changed |= SaveEntryIfChanged(entry, entryChanged || entry.IsDirty);
            }

            if (!changed)
            {
                return false;
            }

            BackupFile(storagePath, "collections");
            WriteJsonAtomically(storagePath, cloudData);
            Log.Debug("Modern Steam collections written to {CollectionStoragePath}", storagePath);
            return true;
        }

        private static List<CollectionEntry> ReadCollectionEntries(JArray cloudData)
        {
            var result = new List<CollectionEntry>();

            foreach (JToken token in cloudData)
            {
                if (token is not JArray item || item.Count < 2 || item[0]?.Type != JTokenType.String || item[1] is not JObject metadata)
                {
                    continue;
                }

                string key = item[0]!.Value<string>()!;
                if (!key.StartsWith(CollectionKeyPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string? serialized = metadata.Value<string>("value");
                if (String.IsNullOrWhiteSpace(serialized))
                {
                    continue;
                }

                try
                {
                    var data = JObject.Parse(serialized);
                    string id = data.Value<string>("id") ?? key.Substring(CollectionKeyPrefix.Length);
                    string name = data.Value<string>("name") ?? id;
                    EnsureArray(data, "added");
                    EnsureArray(data, "removed");
                    result.Add(new CollectionEntry(id, name, metadata, data)
                    {
                        IsDeleted = metadata.Value<bool?>("is_deleted") == true,
                    });
                }
                catch (JsonException exception)
                {
                    Log.Warning(exception, "Ignoring malformed Steam collection entry {CollectionKey}", key);
                }
            }

            return result;
        }

        private static bool AddAppIds(JObject collection, HashSet<long> appIds)
        {
            JArray added = EnsureArray(collection, "added");
            JArray removed = EnsureArray(collection, "removed");
            var addedIds = added.Values<long>().ToHashSet();
            bool changed = false;

            foreach (long appId in appIds)
            {
                if (addedIds.Add(appId))
                {
                    added.Add(appId);
                    changed = true;
                }

                changed |= RemoveAll(removed, appId);
            }

            return changed;
        }

        private static bool RemoveAppIds(JObject collection, HashSet<long> appIds)
        {
            JArray added = EnsureArray(collection, "added");
            JArray removed = EnsureArray(collection, "removed");
            var removedIds = removed.Values<long>().ToHashSet();
            bool changed = false;

            foreach (long appId in appIds)
            {
                bool wasAdded = RemoveAll(added, appId);
                changed |= wasAdded;
                if (wasAdded && removedIds.Add(appId))
                {
                    removed.Add(appId);
                    changed = true;
                }
            }

            return changed;
        }

        private static bool RemoveAll(JArray array, long value)
        {
            bool changed = false;
            for (int index = array.Count - 1; index >= 0; index--)
            {
                if (array[index]?.Type == JTokenType.Integer && array[index]!.Value<long>() == value)
                {
                    array.RemoveAt(index);
                    changed = true;
                }
            }

            return changed;
        }

        private static bool SaveEntryIfChanged(CollectionEntry entry, bool changedEntry)
        {
            if (!changedEntry)
            {
                return false;
            }

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            entry.Metadata.Remove("is_deleted");
            entry.Metadata["key"] = CollectionKeyPrefix + entry.Id;
            entry.Metadata["timestamp"] = timestamp;
            entry.Metadata["value"] = entry.Data.ToString(Formatting.None);
            entry.Metadata["version"] = timestamp.ToString();
            entry.Metadata["conflictResolutionMethod"] = "custom";
            entry.Metadata["strMethodId"] = "union-collections";
            entry.IsDirty = false;
            entry.IsDeleted = false;
            return true;
        }

        private static bool MarkEntryDeleted(CollectionEntry entry)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            entry.Metadata["key"] = CollectionKeyPrefix + entry.Id;
            entry.Metadata["is_deleted"] = true;
            entry.Metadata["timestamp"] = timestamp;
            entry.Metadata["value"] = entry.Data.ToString(Formatting.None);
            entry.Metadata["version"] = timestamp.ToString();
            entry.Metadata["conflictResolutionMethod"] = "custom";
            entry.Metadata["strMethodId"] = "union-collections";
            entry.IsDirty = false;
            entry.IsDeleted = true;
            return true;
        }

        private static JArray EnsureArray(JObject value, string propertyName)
        {
            if (value[propertyName] is JArray array)
            {
                return array;
            }

            array = new JArray();
            value[propertyName] = array;
            return array;
        }

        private static string? GetActiveCloudStoragePath(string userPath)
        {
            string cloudStorageDirectory = Path.Combine(userPath, "config", "cloudstorage");
            string namespacesPath = Path.Combine(cloudStorageDirectory, "cloud-storage-namespaces.json");
            int activeNamespace = 1;

            if (File.Exists(namespacesPath))
            {
                try
                {
                    var namespaces = JArray.Parse(File.ReadAllText(namespacesPath));
                    var active = namespaces
                        .OfType<JArray>()
                        .Where(item => item.Count >= 2 && Int32.TryParse(item[0]?.ToString(), out _) && Int64.TryParse(item[1]?.ToString(), out long version) && version > 0)
                        .OrderByDescending(item => Int64.Parse(item[1]!.ToString()))
                        .FirstOrDefault();

                    if (active != null)
                    {
                        activeNamespace = Int32.Parse(active[0]!.ToString());
                    }
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Could not determine Steam's active cloud-storage namespace for {UserPath}; using namespace 1.", userPath);
                }
            }

            string storagePath = Path.Combine(cloudStorageDirectory, $"cloud-storage-namespace-{activeNamespace}.json");
            return File.Exists(storagePath) ? storagePath : null;
        }

        private static string CreateManagedCollectionId(string collectionName)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(collectionName.ToUpperInvariant()));
            return ManagedCollectionPrefix + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        }

        private static long ToUnsignedAppId(int appId)
        {
            return unchecked((uint)appId);
        }

        private static void BackupFile(string sourcePath, string kind)
        {
            string backupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Briano", "UWPHook", "backups");
            Directory.CreateDirectory(backupFolder);
            string userId = new DirectoryInfo(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(sourcePath))!)!).Name;
            string destination = Path.Combine(backupFolder, $"{userId}_{DateTime.Now:yyyyMMddHHmmssfff}_{kind}{Path.GetExtension(sourcePath)}");
            File.Copy(sourcePath, destination, overwrite: false);
        }

        private static void WriteJsonAtomically(string path, JArray value)
        {
            string temporaryPath = path + ".uwphook.tmp";
            File.WriteAllText(temporaryPath, value.ToString(Formatting.None), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }

        private sealed class CollectionEntry
        {
            public CollectionEntry(string id, string name, JObject metadata, JObject data)
            {
                Id = id;
                Name = name;
                Metadata = metadata;
                Data = data;
            }

            public string Id { get; }
            public string Name { get; }
            public JObject Metadata { get; }
            public JObject Data { get; }
            public bool IsDirty { get; set; }
            public bool IsDeleted { get; set; }
        }
    }
}
