using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using CrowdFund.Ai.Concierge;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat", Chat).RequireRateLimiting("strict");
        app.MapPost("/api/chat/confirm-donation", ConfirmDonationIntent);
    }

    private static async Task<IResult> Chat(
        [FromBody] ChatRequest req,
        AppDbContext db,
        DonorConcierge concierge,
        HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(req.Message))
            return Results.BadRequest(new { error = "Message required" });

        Campaign? campaign = null;
        if (req.CampaignId.HasValue)
            campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CampaignId);

        Donation? donation = null;
        if (req.DonationId.HasValue)
            donation = await db.Donations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == req.DonationId);

        var expenses = campaign == null
            ? new List<Expense>()
            : await db.Expenses.AsNoTracking().Include(e => e.Evidences)
                .Where(e => e.CampaignId == campaign.Id).ToListAsync();

        var searchable = await db.Campaigns.AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Published).Take(50).ToListAsync();

        var reply = await concierge.AnswerAsync(req.Message, campaign, donation, expenses, searchable);

        // Persist chat optionally
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anon";
        var session = await db.ChatSessions.FirstOrDefaultAsync(s =>
            s.UserId == userId && s.CampaignId == req.CampaignId);
        if (session == null)
        {
            session = new ChatSession { UserId = userId, CampaignId = req.CampaignId };
            db.ChatSessions.Add(session);
            await db.SaveChangesAsync();
        }
        db.ChatMessages.Add(new ChatMessage { ChatSessionId = session.Id, Role = "user", Content = req.Message });
        db.ChatMessages.Add(new ChatMessage
        {
            ChatSessionId = session.Id,
            Role = "assistant",
            Content = reply.Answer,
            CitationsJson = System.Text.Json.JsonSerializer.Serialize(reply.Citations),
            ToolCallsJson = System.Text.Json.JsonSerializer.Serialize(reply.ToolsUsed)
        });
        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            answer = reply.Answer,
            citations = reply.Citations,
            toolsUsed = reply.ToolsUsed,
            language = reply.Language,
            groundingOk = reply.GroundingOk,
            prepareDonation = reply.PrepareDonationCard
        });
    }


    private static async Task ChatStream(
        HttpContext http,
        AppDbContext db,
        ConciergeStreaming streaming)
    {
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync();
        var req = System.Text.Json.JsonSerializer.Deserialize<ChatRequest>(body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new ChatRequest("");
        if (string.IsNullOrWhiteSpace(req.Message))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsync("""{"error":"Message required"}""");
            return;
        }

        Campaign? campaign = null;
        if (req.CampaignId.HasValue)
            campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CampaignId);
        Donation? donation = null;
        if (req.DonationId.HasValue)
            donation = await db.Donations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == req.DonationId);
        var expenses = campaign == null
            ? new List<Expense>()
            : await db.Expenses.AsNoTracking().Include(e => e.Evidences)
                .Where(e => e.CampaignId == campaign.Id).ToListAsync();
        var searchable = await db.Campaigns.AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Published).Take(50).ToListAsync();

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await foreach (var chunk in streaming.StreamAnswerAsync(req.Message, campaign, donation, expenses, searchable, http.RequestAborted))
        {
            var data = chunk.Replace("\n", "\\n");
            await http.Response.WriteAsync($"data: {data}\n\n");
            await http.Response.Body.FlushAsync();
        }
        await http.Response.WriteAsync("data: [DONE]\n\n");
        await http.Response.Body.FlushAsync();
    }

    private static IResult ConfirmDonationIntent([FromBody] ConfirmRequest req)
    {
        // Client should navigate to /donate/{campaignId} with amount — agent never captures.
        return Results.Ok(new
        {
            ok = true,
            next = $"/donate/{req.CampaignId}",
            amount = req.Amount,
            message = "Continue on the donation page. PayPal capture is server-side only."
        });
    }

    public record ChatRequest(string Message, Guid? CampaignId = null, Guid? DonationId = null);
    public record ConfirmRequest(Guid CampaignId, decimal Amount, Guid? IntentId = null);
}
