using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Loom.Core;
using Microsoft.Data.Sqlite;

namespace Loom.Sqlite;

/// <summary>Hashes detect accidental corruption, not malicious replacement of an unsigned archive.</summary>
public sealed record BackupManifest(int Format,string TenantId,string SchemaHash,string DatabaseSha256,string CreatedAt);

public sealed partial class SqliteStore
{
    /// <summary>Persist the physical tenant binding; copying another tenant's file fails closed.</summary>
    internal void BindTenant(string tenant,bool allowCreate=false)
    {
        TenantWorkspace.ValidateId(tenant);
        lock(gate)
        {
            using var c=connection.CreateCommand();
            c.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name='_loom_tenant'";
            if(c.ExecuteScalar()==null)
            {
                if(!allowCreate)throw new LoomException(409,"Database has no tenant binding.");
                // Never silently adopt a pre-existing single-tenant database.
                c.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                if(Convert.ToInt64(c.ExecuteScalar())!=0)throw new LoomException(409,"Existing database requires explicit tenant adoption.");
                c.CommandText="CREATE TABLE _loom_tenant(id INTEGER PRIMARY KEY CHECK(id=1), tenant TEXT NOT NULL); INSERT INTO _loom_tenant VALUES(1,$tenant)";
                c.Parameters.AddWithValue("$tenant",tenant);c.ExecuteNonQuery();
            }
            c.CommandText="SELECT tenant FROM _loom_tenant WHERE id=1";
            if(!string.Equals(c.ExecuteScalar() as string,tenant,StringComparison.Ordinal))
                throw new LoomException(409,"Database tenant binding mismatch.");
        }
    }

    /// <summary>SQLite online backup includes committed WAL data, catalog, audit, reports and migration receipts.</summary>
    public void Backup(string archive,string tenant)
    {
        var destination=Path.GetFullPath(archive);
        if(File.Exists(destination))throw new IOException("Backup destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary=Path.Combine(Path.GetDirectoryName(destination)!,".loom-backup-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            var database=Path.Combine(temporary,"documents.db");
            lock(gate)
            {
                BindTenant(tenant);
                using var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Pooling=false}.ToString());
                target.Open();connection.BackupDatabase(target);
            }
            string schemaHash;
            using(var snapshot=new SqliteStore(new SqliteConnectionStringBuilder{DataSource=database,Pooling=false}.ToString()))
            {
                snapshot.BindTenant(tenant);schemaHash=SchemaIdentity.Hash(snapshot.ReadCatalog()??throw new InvalidDataException("Missing backup catalog."));
                snapshot.CheckIntegrity();
                snapshot.CheckpointForExport();
            }
            var manifest=new BackupManifest(1,tenant,schemaHash,Hash(database),DateTimeOffset.UtcNow.ToString("O"));
            var pending=Path.Combine(temporary,"backup.zip");
            using(var zip=ZipFile.Open(pending,ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(database,"documents.db",CompressionLevel.Optimal);
                using var writer=new StreamWriter(zip.CreateEntry("manifest.json").Open());
                writer.Write(JsonSerializer.Serialize(manifest,Catalog.Json));
            }
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(pending,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            File.Move(pending,destination,false);
        }
        finally {Directory.Delete(temporary,true);}
    }

    private void CheckIntegrity()
    {
        using var c=connection.CreateCommand();c.CommandText="PRAGMA integrity_check";
        if(c.ExecuteScalar() as string!="ok")throw new InvalidDataException("SQLite integrity check failed.");
        c.CommandText="PRAGMA foreign_key_check";
        using var reader=c.ExecuteReader();if(reader.Read())throw new InvalidDataException("Foreign key check failed.");
    }
    private void CheckpointForExport()
    {
        using var c=connection.CreateCommand();c.CommandText="PRAGMA wal_checkpoint(TRUNCATE);";c.ExecuteNonQuery();
        c.CommandText="PRAGMA journal_mode=DELETE;";c.ExecuteScalar();
    }
    private static string Hash(string path)
    {using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}

    /// <summary>Validate in a private staging directory, then publish without overwriting any existing database.</summary>
    public static void RestoreBackup(string archive,string database,string expectedTenant,long maximumBytes=536870912)
    {
        TenantWorkspace.ValidateId(expectedTenant);
        if(maximumBytes<=0)throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        database=Path.GetFullPath(database);
        if(File.Exists(database)||File.Exists(database+"-wal")||File.Exists(database+"-shm"))
            throw new IOException("Restore requires a new, empty destination.");
        var parent=Path.GetDirectoryName(database)!;Directory.CreateDirectory(parent);
        var staging=Path.Combine(parent,".loom-restore-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(staging,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            var candidate=Path.Combine(staging,"documents.db");
            BackupManifest manifest;
            using(var zip=ZipFile.OpenRead(archive))
            {
                if(zip.Entries.Count!=2||zip.Entries.Count(e=>e.FullName=="documents.db")!=1||zip.Entries.Count(e=>e.FullName=="manifest.json")!=1)
                    throw new InvalidDataException("Unexpected backup entries.");
                var metadata=zip.GetEntry("manifest.json")!;
                if(metadata.Length>16384)throw new InvalidDataException("Oversized backup manifest.");
                using(var reader=new StreamReader(metadata.Open()))
                    manifest=JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd(),Catalog.Json)??throw new InvalidDataException("Missing manifest.");
                if(manifest.Format!=1||manifest.TenantId!=expectedTenant)throw new InvalidDataException("Backup version or tenant mismatch.");
                var entry=zip.GetEntry("documents.db")!;
                if(entry.Length>maximumBytes)throw new InvalidDataException("Backup exceeds restore size limit.");
                using var input=entry.Open();using var output=new FileStream(candidate,FileMode.CreateNew);
                var buffer=new byte[81920];long length=0;int read;
                while((read=input.Read(buffer))!=0)
                {length=checked(length+read);if(length>maximumBytes)throw new InvalidDataException("Backup exceeds restore size limit.");output.Write(buffer,0,read);}
            }
            if(Hash(candidate)!=manifest.DatabaseSha256)throw new InvalidDataException("Backup checksum mismatch.");
            using(var restored=new SqliteStore(new SqliteConnectionStringBuilder{DataSource=candidate,Pooling=false,Mode=SqliteOpenMode.ReadWrite}.ToString()))
            {
                restored.BindTenant(expectedTenant);restored.CheckIntegrity();
                var catalog=restored.ReadCatalog()??throw new InvalidDataException("Missing restored catalog.");
                if(SchemaIdentity.Hash(catalog)!=manifest.SchemaHash)throw new InvalidDataException("Backup schema mismatch.");
                restored.CheckpointForExport();
            }
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(candidate,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            File.Move(candidate,database,false);
        }
        finally {Directory.Delete(staging,true);}
    }
}
