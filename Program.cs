using MemoryOpsAI.Services;

var builder = WebApplication.CreateBuilder(args);

// Controllers + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// HttpClient-backed services (Hindsight + Groq wiring lands in Phase 2/3)
builder.Services.AddHttpClient<IHindsightService, HindsightService>();
builder.Services.AddHttpClient<IAgentService, AgentService>();

// CORS - open for local hackathon dev; tighten before sharing publicly
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

app.Run();
