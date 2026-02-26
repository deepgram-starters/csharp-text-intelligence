/**
 * C# Text Intelligence Starter - Backend Server
 *
 * This is a minimal API server providing text intelligence analysis
 * powered by Deepgram's Text Intelligence service. It's designed to be
 * easily modified and extended for your own projects.
 *
 * Key Features:
 * - Contract-compliant API endpoint: POST /api/text-intelligence
 * - Accepts text or URL in JSON body
 * - Supports multiple intelligence features: summarization, topics, sentiment, intents
 * - JWT session auth with rate limiting (production only)
 * - CORS enabled for frontend communication
 */

using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Deepgram;
using Deepgram.Models.Analyze.v1;
using Microsoft.IdentityModel.Tokens;
using Tomlyn;
using Tomlyn.Model;
using HttpResults = Microsoft.AspNetCore.Http.Results;

// ============================================================================
// ENVIRONMENT LOADING
// ============================================================================

DotNetEnv.Env.Load();

// ============================================================================
// CONFIGURATION
// ============================================================================

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8081;
var host = Environment.GetEnvironmentVariable("HOST") ?? "0.0.0.0";

// ============================================================================
// SESSION AUTH - JWT tokens for production security
// ============================================================================

var sessionSecretEnv = Environment.GetEnvironmentVariable("SESSION_SECRET");
var sessionSecret = sessionSecretEnv ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
var sessionSecretKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(sessionSecret));

const int JwtExpirySeconds = 3600;

string CreateSessionToken()
{
    var handler = new JwtSecurityTokenHandler();
    var descriptor = new SecurityTokenDescriptor
    {
        Expires = DateTime.UtcNow.AddSeconds(JwtExpirySeconds),
        SigningCredentials = new SigningCredentials(sessionSecretKey, SecurityAlgorithms.HmacSha256Signature),
    };
    var token = handler.CreateToken(descriptor);
    return handler.WriteToken(token);
}

bool ValidateSessionToken(string token)
{
    try
    {
        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = sessionSecretKey,
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero,
        }, out _);
        return true;
    }
    catch
    {
        return false;
    }
}

// ============================================================================
// API KEY LOADING
// ============================================================================

static string LoadApiKey()
{
    var apiKey = Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY");

    if (string.IsNullOrEmpty(apiKey))
    {
        Console.Error.WriteLine("\n❌ ERROR: Deepgram API key not found!\n");
        Console.Error.WriteLine("Please set your API key in .env file:");
        Console.Error.WriteLine("   DEEPGRAM_API_KEY=your_api_key_here\n");
        Console.Error.WriteLine("Get your API key at: https://console.deepgram.com\n");
        Environment.Exit(1);
    }

    return apiKey;
}

var apiKey = LoadApiKey();

// ============================================================================
// SETUP
// ============================================================================

Library.Initialize();
var deepgramClient = ClientFactory.CreateAnalyzeClient(apiKey);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{host}:{port}");

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors();

// ============================================================================
// SESSION ROUTES
// ============================================================================

app.MapGet("/api/session", () =>
{
    var token = CreateSessionToken();
    return HttpResults.Json(new Dictionary<string, string> { ["token"] = token });
});

// ============================================================================
// API ROUTES
// ============================================================================

/// POST /api/text-intelligence
///
/// Contract-compliant text intelligence endpoint.
/// Accepts:
/// - Query parameters: summarize, topics, sentiment, intents, language (all optional)
/// - Body: JSON with either text or url field (required, not both)
app.MapPost("/api/text-intelligence", async (HttpRequest request) =>
{
    // Validate JWT session token
    var authHeader = request.Headers.Authorization.FirstOrDefault() ?? "";
    if (!authHeader.StartsWith("Bearer "))
    {
        return HttpResults.Json(new Dictionary<string, object>
        {
            ["error"] = new Dictionary<string, string>
            {
                ["type"] = "AuthenticationError",
                ["code"] = "MISSING_TOKEN",
                ["message"] = "Authorization header with Bearer token is required",
            }
        }, statusCode: 401);
    }
    if (!ValidateSessionToken(authHeader[7..]))
    {
        return HttpResults.Json(new Dictionary<string, object>
        {
            ["error"] = new Dictionary<string, string>
            {
                ["type"] = "AuthenticationError",
                ["code"] = "INVALID_TOKEN",
                ["message"] = "Invalid or expired session token",
            }
        }, statusCode: 401);
    }

    try
    {
        // Read JSON body
        var body = await request.ReadFromJsonAsync<Dictionary<string, string>>();
        var text = body?.GetValueOrDefault("text");
        var url = body?.GetValueOrDefault("url");

        // Validate that exactly one of text or url is provided
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(url))
        {
            return HttpResults.Json(new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, string>
                {
                    ["type"] = "validation_error",
                    ["code"] = "INVALID_TEXT",
                    ["message"] = "Request must contain either 'text' or 'url' field",
                }
            }, statusCode: 400);
        }

        if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(url))
        {
            return HttpResults.Json(new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, string>
                {
                    ["type"] = "validation_error",
                    ["code"] = "INVALID_TEXT",
                    ["message"] = "Request must contain either 'text' or 'url', not both",
                }
            }, statusCode: 400);
        }

        // Get the text content (either directly or from URL)
        string textContent;

        if (!string.IsNullOrEmpty(url))
        {
            // Validate URL format
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                return HttpResults.Json(new Dictionary<string, object>
                {
                    ["error"] = new Dictionary<string, string>
                    {
                        ["type"] = "validation_error",
                        ["code"] = "INVALID_URL",
                        ["message"] = "Invalid URL format",
                    }
                }, statusCode: 400);
            }

            // Fetch text from URL
            try
            {
                using var httpClient = new HttpClient();
                var response = await httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    return HttpResults.Json(new Dictionary<string, object>
                    {
                        ["error"] = new Dictionary<string, string>
                        {
                            ["type"] = "validation_error",
                            ["code"] = "INVALID_URL",
                            ["message"] = $"Failed to fetch URL: {response.StatusCode}",
                        }
                    }, statusCode: 400);
                }
                textContent = await response.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
                return HttpResults.Json(new Dictionary<string, object>
                {
                    ["error"] = new Dictionary<string, string>
                    {
                        ["type"] = "validation_error",
                        ["code"] = "INVALID_URL",
                        ["message"] = $"Failed to fetch URL: {e.Message}",
                    }
                }, statusCode: 400);
            }
        }
        else
        {
            textContent = text!;
        }

        // Check for empty text
        if (string.IsNullOrWhiteSpace(textContent))
        {
            return HttpResults.Json(new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, string>
                {
                    ["type"] = "validation_error",
                    ["code"] = "EMPTY_TEXT",
                    ["message"] = "Text content cannot be empty",
                }
            }, statusCode: 400);
        }

        // Extract query parameters for intelligence features
        var language = request.Query["language"].FirstOrDefault() ?? "en";
        var summarize = request.Query["summarize"].FirstOrDefault();
        var topics = request.Query["topics"].FirstOrDefault();
        var sentiment = request.Query["sentiment"].FirstOrDefault();
        var intents = request.Query["intents"].FirstOrDefault();

        // Build Deepgram options
        var schema = new AnalyzeSchema
        {
            Language = language,
        };

        if (summarize == "true" || summarize == "v2")
        {
            schema.Summarize = true;
        }
        else if (summarize == "v1")
        {
            return HttpResults.Json(new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, string>
                {
                    ["type"] = "validation_error",
                    ["code"] = "INVALID_TEXT",
                    ["message"] = "Summarization v1 is no longer supported. Please use v2 or true.",
                }
            }, statusCode: 400);
        }

        if (topics == "true") schema.Topics = true;
        if (sentiment == "true") schema.Sentiment = true;
        if (intents == "true") schema.Intents = true;

        // Call Deepgram API
        var result = await deepgramClient.AnalyzeText(
            new TextSource(textContent),
            schema);

        if (result == null)
        {
            throw new InvalidOperationException("Deepgram returned null response");
        }

        // Return results using raw JSON serialization for proper formatting
        var resultJson = JsonSerializer.Serialize(new
        {
            results = result.Results ?? new object()
        });

        return HttpResults.Content(resultJson, "application/json");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Text Intelligence Error: {ex}");

        var errorCode = "INVALID_TEXT";
        var statusCode = 500;

        var msg = ex.Message.ToLowerInvariant();
        if (msg.Contains("text"))
        {
            errorCode = "INVALID_TEXT";
            statusCode = 400;
        }
        else if (msg.Contains("too long"))
        {
            errorCode = "TEXT_TOO_LONG";
            statusCode = 400;
        }

        return HttpResults.Json(new Dictionary<string, object>
        {
            ["error"] = new Dictionary<string, string>
            {
                ["type"] = "processing_error",
                ["code"] = errorCode,
                ["message"] = ex.Message,
            }
        }, statusCode: statusCode);
    }
});

// Health check endpoint
app.MapGet("/health", () => HttpResults.Json(new { status = "ok", service = "text-intelligence" }));

// Metadata endpoint
app.MapGet("/api/metadata", () =>
{
    try
    {
        var tomlPath = Path.Combine(Directory.GetCurrentDirectory(), "deepgram.toml");
        var tomlContent = File.ReadAllText(tomlPath);
        var tomlModel = Toml.ToModel(tomlContent);

        if (!tomlModel.ContainsKey("meta") || tomlModel["meta"] is not TomlTable metaTable)
        {
            return HttpResults.Json(new Dictionary<string, string>
            {
                ["error"] = "INTERNAL_SERVER_ERROR",
                ["message"] = "Missing [meta] section in deepgram.toml",
            }, statusCode: 500);
        }

        var meta = new Dictionary<string, object?>();
        foreach (var kvp in metaTable)
        {
            meta[kvp.Key] = kvp.Value;
        }

        return HttpResults.Json(meta);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error reading metadata: {ex}");
        return HttpResults.Json(new Dictionary<string, string>
        {
            ["error"] = "INTERNAL_SERVER_ERROR",
            ["message"] = "Failed to read metadata from deepgram.toml",
        }, statusCode: 500);
    }
});

// ============================================================================
// SERVER START
// ============================================================================

Console.WriteLine();
Console.WriteLine(new string('=', 70));
Console.WriteLine($"🚀 Backend API Server running at http://localhost:{port}");
Console.WriteLine($"📡 CORS enabled for all origins");
Console.WriteLine($"📡 GET  /api/session");
Console.WriteLine($"📡 POST /api/text-intelligence (auth required)");
Console.WriteLine($"📡 GET  /api/metadata");
Console.WriteLine(new string('=', 70));
Console.WriteLine();

app.Run();
