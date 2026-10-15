using DocSearch.Domain;
using DocSearch.Domain.Constants;
using DocSearch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DocSearch.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionMember> CollectionMembers => Set<CollectionMember>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ReindexJob> ReindexJobs => Set<ReindexJob>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasPostgresExtension("vector");
        builder.HasPostgresExtension("pg_trgm");

        ConfigureCollections(builder);
        ConfigureCollectionMembers(builder);
        ConfigureDocuments(builder);
        ConfigureChunks(builder);
        ConfigureConversations(builder);
        ConfigureMessages(builder);
        ConfigureReindexJobs(builder);
    }

    private static void ConfigureCollections(ModelBuilder builder)
    {
        builder.Entity<Collection>(b =>
        {
            b.ToTable("collections");

            b.HasKey(c => c.Id);
            b.Property(c => c.Id)
                .HasColumnName("id");

            b.Property(c => c.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();

            b.Property(c => c.Description)
                .HasColumnName("description")
                .HasMaxLength(2000);

            b.Property(c => c.OwnerId)
                .HasColumnName("owner_id")
                .HasColumnType("text")
                .IsRequired();

            b.Property(c => c.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()")
                .IsRequired();

            b.HasIndex(c => new { c.OwnerId, c.Name })
                .IsUnique();

            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(c => c.OwnerId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureCollectionMembers(ModelBuilder builder)
    {
        builder.Entity<CollectionMember>(b =>
        {
            b.ToTable("collection_members", t =>
            {
                t.HasCheckConstraint("ck_collection_members_role", "role IN ('Viewer','Editor','Owner')");
            });

            b.HasKey(m => new { m.CollectionId, m.UserId });

            b.Property(m => m.CollectionId)
                .HasColumnName("collection_id");

            b.Property(m => m.UserId)
                .HasColumnName("user_id")
                .HasColumnType("text");

            b.Property(m => m.Role)
                .HasColumnName("role")
                .HasColumnType("text")
                .HasConversion<string>()
                .IsRequired();

            b.HasOne(m => m.Collection)
                .WithMany(c => c.Members)
                .HasForeignKey(m => m.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureDocuments(ModelBuilder builder)
    {
        builder.Entity<Document>(b =>
        {
            b.ToTable("documents", t =>
            {
                t.HasCheckConstraint("ck_documents_status", "status IN ('Uploaded','Processing','Ready','Failed')");
            });

            b.HasKey(d => d.Id);
            b.Property(d => d.Id)
                .HasColumnName("id");

            b.Property(d => d.CollectionId)
                .HasColumnName("collection_id");

            b.Property(d => d.FileName)
                .HasColumnName("file_name")
                .HasMaxLength(500)
                .IsRequired();

            b.Property(d => d.StoragePath)
                .HasColumnName("storage_path")
                .HasMaxLength(1000)
                .IsRequired();

            b.Property(d => d.ContentType)
                .HasColumnName("content_type")
                .HasMaxLength(100)
                .IsRequired();

            b.Property(d => d.SizeBytes)
                .HasColumnName("size_bytes")
                .IsRequired();

            b.Property(d => d.ContentHash)
                .HasColumnName("content_hash")
                .HasColumnType("char(64)")
                .IsFixedLength()
                .HasMaxLength(64)
                .IsRequired();

            b.Property(d => d.PageCount)
                .HasColumnName("page_count");

            b.Property(d => d.OcrPages)
                .HasColumnName("ocr_pages")
                .HasColumnType("integer[]");

            b.Property(d => d.Status)
                .HasColumnName("status")
                .HasColumnType("text")
                .HasConversion<string>()
                .IsRequired();

            b.Property(d => d.ErrorMessage)
                .HasColumnName("error_message")
                .HasMaxLength(2000);

            b.Property(d => d.UploadedBy)
                .HasColumnName("uploaded_by")
                .HasColumnType("text")
                .IsRequired();

            b.Property(d => d.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()")
                .IsRequired();

            b.Property(d => d.ProcessedAt)
                .HasColumnName("processed_at")
                .HasColumnType("timestamp with time zone");

            b.HasIndex(d => new { d.CollectionId, d.ContentHash })
                .IsUnique();

            b.HasIndex(d => new { d.CollectionId, d.Status })
                .HasDatabaseName("ix_documents_collection");

            b.HasOne(d => d.Collection)
                .WithMany(c => c.Documents)
                .HasForeignKey(d => d.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(d => d.UploadedBy)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureChunks(ModelBuilder builder)
    {
        builder.Entity<Chunk>(b =>
        {
            b.ToTable("chunks");

            b.HasKey(c => c.Id);
            b.Property(c => c.Id)
                .HasColumnName("id");

            b.Property(c => c.DocumentId)
                .HasColumnName("document_id");

            b.Property(c => c.CollectionId)
                .HasColumnName("collection_id");

            b.Property(c => c.ChunkIndex)
                .HasColumnName("chunk_index");

            b.Property(c => c.PageStart)
                .HasColumnName("page_start");

            b.Property(c => c.PageEnd)
                .HasColumnName("page_end");

            b.Property(c => c.SectionTitle)
                .HasColumnName("section_title")
                .HasMaxLength(500);

            b.Property(c => c.Text)
                .HasColumnName("text")
                .HasColumnType("text")
                .IsRequired();

            b.Property(c => c.SearchText)
                .HasColumnName("search_text")
                .HasColumnType("text")
                .IsRequired();

            b.Property(c => c.TokenCount)
                .HasColumnName("token_count");

            b.Property(c => c.Embedding)
                .HasColumnName("embedding")
                .HasColumnType($"vector({EmbeddingConstants.VectorDimensions})")
                .IsRequired();

            b.Property(c => c.EmbeddingModel)
                .HasColumnName("embedding_model")
                .HasMaxLength(200)
                .IsRequired();

            b.Property(c => c.Tsv)
                .HasColumnName("tsv")
                .HasColumnType("tsvector")
                .HasComputedColumnSql("to_tsvector('simple', search_text)", stored: true);

            b.HasIndex(c => new { c.DocumentId, c.ChunkIndex })
                .IsUnique();

            b.HasIndex(c => c.CollectionId)
                .HasDatabaseName("ix_chunks_collection");

            b.HasIndex(c => c.Embedding)
                .HasDatabaseName("ix_chunks_embedding_hnsw")
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops")
                .HasStorageParameter("m", 16)
                .HasStorageParameter("ef_construction", 64);

            b.HasIndex(c => c.Tsv)
                .HasDatabaseName("ix_chunks_tsv")
                .HasMethod("gin");

            b.HasIndex(c => c.SearchText)
                .HasDatabaseName("ix_chunks_search_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");

            b.HasOne(c => c.Document)
                .WithMany(d => d.Chunks)
                .HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureConversations(ModelBuilder builder)
    {
        builder.Entity<Conversation>(b =>
        {
            b.ToTable("conversations");

            b.HasKey(c => c.Id);
            b.Property(c => c.Id)
                .HasColumnName("id");

            b.Property(c => c.UserId)
                .HasColumnName("user_id")
                .HasColumnType("text")
                .IsRequired();

            b.Property(c => c.Title)
                .HasColumnName("title")
                .HasMaxLength(300);

            b.Property(c => c.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()")
                .IsRequired();

            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMessages(ModelBuilder builder)
    {
        builder.Entity<Message>(b =>
        {
            b.ToTable("messages", t =>
            {
                t.HasCheckConstraint("ck_messages_role", "role IN ('user','assistant')");
            });

            b.HasKey(m => m.Id);
            b.Property(m => m.Id)
                .HasColumnName("id");

            b.Property(m => m.ConversationId)
                .HasColumnName("conversation_id");

            b.Property(m => m.Role)
                .HasColumnName("role")
                .HasColumnType("text")
                .HasConversion(
                    r => r.ToString().ToLowerInvariant(),
                    s => Enum.Parse<MessageRole>(s, true))
                .IsRequired();

            b.Property(m => m.Content)
                .HasColumnName("content")
                .HasColumnType("text")
                .IsRequired();

            b.Property(m => m.SourcesJson)
                .HasColumnName("sources_json")
                .HasColumnType("jsonb");

            b.Property(m => m.Degraded)
                .HasColumnName("degraded")
                .HasDefaultValue(false)
                .IsRequired();

            b.Property(m => m.NotFound)
                .HasColumnName("not_found")
                .HasDefaultValue(false)
                .IsRequired();

            b.Property(m => m.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()")
                .IsRequired();

            b.HasIndex(m => new { m.ConversationId, m.CreatedAt })
                .HasDatabaseName("ix_messages_conv");

            b.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureReindexJobs(ModelBuilder builder)
    {
        builder.Entity<ReindexJob>(b =>
        {
            b.ToTable("reindex_jobs", t =>
            {
                t.HasCheckConstraint("ck_reindex_jobs_status", "status IN ('Queued','Running','Completed','Failed')");
            });

            b.HasKey(j => j.Id);
            b.Property(j => j.Id)
                .HasColumnName("id");

            b.Property(j => j.CollectionId)
                .HasColumnName("collection_id");

            b.Property(j => j.Status)
                .HasColumnName("status")
                .HasColumnType("text")
                .HasConversion<string>()
                .IsRequired();

            b.Property(j => j.TotalChunks)
                .HasColumnName("total_chunks")
                .HasDefaultValue(0)
                .IsRequired();

            b.Property(j => j.DoneChunks)
                .HasColumnName("done_chunks")
                .HasDefaultValue(0)
                .IsRequired();

            b.Property(j => j.EmbeddingModel)
                .HasColumnName("embedding_model")
                .HasMaxLength(200)
                .IsRequired();

            b.Property(j => j.ErrorMessage)
                .HasColumnName("error_message")
                .HasMaxLength(2000);

            b.Property(j => j.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()")
                .IsRequired();

            b.Property(j => j.FinishedAt)
                .HasColumnName("finished_at")
                .HasColumnType("timestamp with time zone");

            b.HasOne(j => j.Collection)
                .WithMany(c => c.ReindexJobs)
                .HasForeignKey(j => j.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
