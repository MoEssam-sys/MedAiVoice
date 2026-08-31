using AiVoice.Components;
using AiVoice.Services;
using MudBlazor.Services;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// إضافة MudBlazor
builder.Services.AddMudServices();

// إضافة الـ AI Service الخاصة بنا
builder.Services.AddScoped<AiService>();
builder.Services.AddControllers();

builder.Services.AddHttpClient<OpenAiMedicalVoiceService>(
    client =>
    {
        client.BaseAddress =
            new Uri("https://api.openai.com/");

        client.Timeout =
            TimeSpan.FromMinutes(5);
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();


app.MapControllers();


app.Run();
