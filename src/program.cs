using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Bogus;
using System.Text.Json;
using System.IO;

BenchmarkRunner.Run<EfCoreQueryBenchmarks>();

[MemoryDiagnoser]
public class EfCoreQueryBenchmarks : IDisposable
{
    private DbContextOptions<BlogContext> options = null!;
    private List<User> users = null!;

    [Params(1, 100)] public int BlogCount { get; set; }
    [Params(100, 1)] public int PostsPerBlog { get; set; }
    [Params(1, 10)] public int CommentsPerPost { get; set; }
    [Params(1, 5)] public int OwnerCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var connection = new SqlConnection("Server=localhost,1433;Database=EfCoreBenchmarkDb;User Id=sa;Password=Your_password123;Encrypt=False;TrustServerCertificate=True");

        options = new DbContextOptionsBuilder<BlogContext>()
            .UseSqlServer(connection)
            .EnableSensitiveDataLogging()
            .EnableDetailedErrors()
            .Options;

        using var context = new BlogContext(options);

        Console.WriteLine($"Preparing data: Blogs={BlogCount}, PostsPerBlog={PostsPerBlog}, CommentsPerPost={CommentsPerPost}, Owners={OwnerCount}");

        context.Database.EnsureCreated();

        context.Comments.RemoveRange(context.Comments);
        context.Posts.RemoveRange(context.Posts);
        context.Blogs.RemoveRange(context.Blogs);
        context.Users.RemoveRange(context.Users);
        context.SaveChanges();

        users = new List<User>();
        for (int i = 1; i <= OwnerCount; i++)
        {
            users.Add(new User { Email = $"user{i}@email.com" });
        }
        context.Users.AddRange(users);
        context.SaveChanges();

        var commentFaker = new Faker<Comment>()
            .RuleFor(c => c.Text, f => f.Lorem.Sentence())
            .RuleFor(c => c.OwnerId, f => f.PickRandom(users).Email);

        var postFaker = new Faker<Post>()
            .RuleFor(p => p.Title, f => f.Lorem.Sentence())
            .RuleFor(p => p.OwnerId, f => f.PickRandom(users).Email)
            .RuleFor(p => p.Comments, (f, p) =>
            {
                var comments = new List<Comment>();
                for (int i = 0; i < CommentsPerPost; i++)
                {
                    var comment = commentFaker.Generate();
                    comment.Post = p;
                    comments.Add(comment);
                }
                return comments;
            });

        var blogs = new List<Blog>();
        for (int i = 0; i < BlogCount; i++)
        {
            if (i % Math.Max(1, BlogCount / 10) == 0)
                Console.WriteLine($"Generating blog {i + 1} of {BlogCount}...");

            var owner = users[i % users.Count];
            var posts = postFaker.Generate(PostsPerBlog);
            foreach (var post in posts)
            {
                post.BlogId = 0;
                post.Blog = null;
            }

            blogs.Add(new Blog
            {
                Url = $"https://blog{i}.com",
                OwnerId = owner.Email,
                Owner = owner,
                Posts = posts
            });
        }

        context.Blogs.AddRange(blogs);
        context.SaveChanges();

        Console.WriteLine("Data generated and saved.");
    }

    [Benchmark(Description = "SingleQuery - Include Posts, Comments & Owners")]
    public void SingleQuery_IncludePostsAndCommentsAndOwners()
    {
        using var context = new BlogContext(options);
        var result = context.Blogs
            .Include(b => b.Owner)
            .Include(b => b.Posts).ThenInclude(p => p.Owner)
            .Include(b => b.Posts).ThenInclude(p => p.Comments).ThenInclude(c => c.Owner)
            .AsSingleQuery()
            .ToList();
    }

    [Benchmark(Description = "SplitQuery - Include Posts, Comments & Owners")]
    public void SplitQuery_IncludePostsAndCommentsAndOwners()
    {
        using var context = new BlogContext(options);
        var result = context.Blogs
            .Include(b => b.Owner)
            .Include(b => b.Posts).ThenInclude(p => p.Owner)
            .Include(b => b.Posts).ThenInclude(p => p.Comments).ThenInclude(c => c.Owner)
            .AsSplitQuery()
            .ToList();
    }

    [Benchmark(Baseline = true, Description = "Raw SQL - Include Posts, Comments & Owners")]
    public void RawSql_IncludePostsAndCommentsAndOwners()
    {
        using var connection = new SqlConnection("Server=localhost,1433;Database=EfCoreBenchmarkDb;User Id=sa;Password=Your_password123;Encrypt=False;TrustServerCertificate=True");
        connection.Open();

        string sql = @"
            SELECT 
                b.BlogId, b.Url, b.OwnerId AS BlogOwnerId,
                u.Email AS BlogOwnerEmail,
                p.PostId, p.Title, p.BlogId AS PostBlogId, p.OwnerId AS PostOwnerId,
                up.Email AS PostOwnerEmail,
                c.CommentId, c.Text, c.PostId AS CommentPostId, c.OwnerId AS CommentOwnerId,
                uc.Email AS CommentOwnerEmail
            FROM Blogs b
            INNER JOIN Users u ON b.OwnerId = u.Email
            LEFT JOIN Posts p ON b.BlogId = p.BlogId
            LEFT JOIN Users up ON p.OwnerId = up.Email
            LEFT JOIN Comments c ON p.PostId = c.PostId
            LEFT JOIN Users uc ON c.OwnerId = uc.Email
        ";

        var blogs = new Dictionary<int, dynamic>();

        using var cmd = new SqlCommand(sql, connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int blogId = reader.GetInt32(0);
            if (!blogs.TryGetValue(blogId, out dynamic blog))
            {
                blog = new
                {
                    BlogId = blogId,
                    Url = reader.GetString(1),
                    OwnerId = reader.GetString(2),
                    OwnerEmail = reader.GetString(3),
                    Posts = new Dictionary<int, dynamic>()
                };
                blogs.Add(blogId, blog);
            }

            if (!reader.IsDBNull(4))
            {
                int postId = reader.GetInt32(4);
                var posts = blog.Posts;
                if (!posts.TryGetValue(postId, out dynamic post))
                {
                    post = new
                    {
                        PostId = postId,
                        Title = reader.GetString(5),
                        BlogId = reader.GetInt32(6),
                        OwnerId = reader.IsDBNull(7) ? null : reader.GetString(7),
                        OwnerEmail = reader.IsDBNull(8) ? null : reader.GetString(8),
                        Comments = new List<dynamic>()
                    };
                    posts.Add(postId, post);
                }

                if (!reader.IsDBNull(9))
                {
                    var comment = new
                    {
                        CommentId = reader.GetInt32(9),
                        Text = reader.GetString(10),
                        PostId = reader.GetInt32(11),
                        OwnerId = reader.IsDBNull(12) ? null : reader.GetString(12),
                        OwnerEmail = reader.IsDBNull(13) ? null : reader.GetString(13)
                    };
                    post.Comments.Add(comment);
                }
            }
        }
    }

    public void Dispose()
    {
    }
}

public class User
{
    public required string Email { get; set; }
    public List<Blog> Blogs { get; set; } = new();
    public List<Post> Posts { get; set; } = new();
    public List<Comment> Comments { get; set; } = new();
}

public class Blog
{
    public int BlogId { get; set; }
    public required string Url { get; set; }
    public required string OwnerId { get; set; }
    public required User Owner { get; set; }
    public List<Post> Posts { get; set; } = new();
}

public class Post
{
    public int PostId { get; set; }
    public required string Title { get; set; }
    public int BlogId { get; set; }
    public Blog? Blog { get; set; }
    public required string OwnerId { get; set; }
    public required User Owner { get; set; }
    public List<Comment> Comments { get; set; } = new();
}

public class Comment
{
    public int CommentId { get; set; }
    public required string Text { get; set; }
    public int PostId { get; set; }
    public Post? Post { get; set; }
    public required string OwnerId { get; set; }
    public required User Owner { get; set; }
}

public class BlogContext : DbContext
{
    public BlogContext(DbContextOptions<BlogContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Comment> Comments => Set<Comment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasKey(u => u.Email);

        modelBuilder.Entity<Blog>()
            .HasOne(b => b.Owner)
            .WithMany(u => u.Blogs)
            .HasForeignKey(b => b.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Post>()
            .HasOne(p => p.Owner)
            .WithMany(u => u.Posts)
            .HasForeignKey(p => p.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Comment>()
            .HasOne<Post>()
            .WithMany(p => p.Comments)
            .HasForeignKey(c => c.PostId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Comment>()
            .HasOne<User>("Owner")
            .WithMany(u => u.Comments)
            .HasForeignKey("OwnerId")
            .OnDelete(DeleteBehavior.Restrict);
    }
}