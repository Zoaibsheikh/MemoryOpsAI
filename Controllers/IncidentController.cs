using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using MemoryOpsAI.Models;
using MemoryOpsAI.Services;

namespace MemoryOpsAI.Controllers
{
    [ApiController]
    [Route("api/incident")]
    public class IncidentController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IHindsightService _hindsight;
        private readonly IAgentService _agent;

        public IncidentController(IConfiguration config, IHindsightService hindsight, IAgentService agent)
        {
            _config = config;
            _hindsight = hindsight;
            _agent = agent;
        }

        private SqlConnection Db() => new SqlConnection(_config.GetConnectionString("MemoryOpsDb"));
        private static string S(SqlDataReader r, string c) => r.IsDBNull(r.GetOrdinal(c)) ? "" : r.GetString(r.GetOrdinal(c));

        // 1) Log incident in SQL  2) RECALL memory  3) Groq suggests a fix.
        // Memory is only written when the outcome is confirmed (see Resolve).
        [HttpPost("analyze")]
        public async Task<ActionResult<AgentResponse>> Analyze([FromBody] IncidentRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Issue))
                return BadRequest("Issue description is required.");

            int id;
            await using (var conn = Db())
            {
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(@"INSERT INTO Incidents (CustomerName, ModuleName, IssueDescription, ErrorDetails, ResolutionStatus)
                    OUTPUT INSERTED.IncidentId VALUES (@c,@m,@i,@e,'Open')", conn);
                cmd.Parameters.AddWithValue("@c", request.Customer ?? "");
                cmd.Parameters.AddWithValue("@m", request.Module ?? "");
                cmd.Parameters.AddWithValue("@i", request.Issue);
                cmd.Parameters.AddWithValue("@e", request.Error ?? "");
                id = (int)(await cmd.ExecuteScalarAsync())!;
            }

            var similar = await _hindsight.RecallAsync($"{request.Module}: {request.Issue}. Error: {request.Error}", 5);
            var (fix, reasoning) = await _agent.ReflectAsync(request, similar);

            return Ok(new AgentResponse
            {
                IncidentId = id,
                RecommendedFix = fix,
                Reasoning = reasoning,
                SimilarPastIncidents = similar,
                Status = similar.Count > 0 ? "Analyzed with memory" : "Analyzed (no memory yet)"
            });
        }

        // Feedback: did the fix work? Confirmed outcome is what the agent learns (RETAIN).
        [HttpPost("{id:int}/resolve")]
        public async Task<IActionResult> Resolve(int id, [FromBody] ResolveRequest body)
        {
            string customer, module, issue, error;
            await using var conn = Db();
            await conn.OpenAsync();

            await using (var get = new SqlCommand("SELECT * FROM Incidents WHERE IncidentId=@id", conn))
            {
                get.Parameters.AddWithValue("@id", id);
                await using var r = await get.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return NotFound();
                customer = S(r, "CustomerName"); module = S(r, "ModuleName");
                issue = S(r, "IssueDescription"); error = S(r, "ErrorDetails");
            }

            var finalFix = string.IsNullOrWhiteSpace(body.ActualFix) ? body.AiSuggestion : body.ActualFix;
            if (string.IsNullOrWhiteSpace(finalFix)) return BadRequest("Please describe the fix that worked.");

            await using (var upd = new SqlCommand("UPDATE Incidents SET Resolution=@f, ResolutionStatus='Resolved' WHERE IncidentId=@id", conn))
            {
                upd.Parameters.AddWithValue("@f", finalFix);
                upd.Parameters.AddWithValue("@id", id);
                await upd.ExecuteNonQueryAsync();
            }

            var memory = $"Resolved incident #{id} in module {module} for {customer}. Issue: {issue}. Error: {error}. ";
            memory += body.Worked
                ? $"Confirmed fix that worked: {finalFix}"
                : $"The suggested fix \"{body.AiSuggestion}\" did NOT work. The fix that actually worked: {finalFix}";
            await _hindsight.RetainAsync(memory);

            return Ok(new { message = "Saved. The agent will remember this next time." });
        }

        [HttpGet]
        public async Task<IActionResult> List()
        {
            var list = new List<object>();
            await using var conn = Db();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT TOP 8 * FROM Incidents ORDER BY IncidentId DESC", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new { id = r.GetInt32(r.GetOrdinal("IncidentId")), customer = S(r, "CustomerName"), module = S(r, "ModuleName"),
                               issue = S(r, "IssueDescription"), status = S(r, "ResolutionStatus"), resolution = S(r, "Resolution") });
            return Ok(list);
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            await using var conn = Db();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT * FROM Incidents WHERE IncidentId=@id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return NotFound();
            return Ok(new { incidentId = id, customer = S(r, "CustomerName"), module = S(r, "ModuleName"), issue = S(r, "IssueDescription"),
                            error = S(r, "ErrorDetails"), resolution = S(r, "Resolution"), status = S(r, "ResolutionStatus") });
        }

        // Loads realistic past incidents (SQL + Hindsight memory) so the demo has history.
        [HttpPost("seed")]
        public async Task<IActionResult> Seed()
        {
            await using var conn = Db();
            await conn.OpenAsync();
            await using (var chk = new SqlCommand("SELECT COUNT(*) FROM Incidents WHERE ResolutionStatus='Resolved'", conn))
                if ((int)(await chk.ExecuteScalarAsync())! >= 10)
                    return Ok(new { message = "Demo history is already loaded." });

            var data = new (string c, string m, string i, string e, string f)[]
            {
                ("ABC Hospital","Appointment","Patients not getting appointment reminder SMS","Scheduler service did not trigger at 8 AM","Windows Task Scheduler job had stopped after a server patch reboot. Restarted the ReminderScheduler service and set recovery option to auto-restart on failure."),
                ("City Care Clinic","Appointment","Reminder SMS sent twice to patients","Duplicate job execution in scheduler log","Two scheduler instances were running after a failover. Disabled the standby instance and added a distributed lock on the job."),
                ("Sunrise Medical","Billing","Invoice generation fails at month end","SqlException: Timeout expired on sp_GenerateInvoices","Missing index on Billing.ChargeDate. Added the index and raised command timeout to 120s; invoices generated normally."),
                ("ABC Hospital","Billing","Insurance claim rejected with wrong amount","Claim payload total does not match invoice total","Tax rounding bug in ClaimBuilder. Switched to decimal rounding per line item and resubmitted claims."),
                ("Lotus Health","Login","Doctors cannot log in after 6 PM","HTTP 401 token expired immediately","Server clock drifted 40 minutes from the identity server. Re-synced NTP and enabled automatic time sync."),
                ("City Care Clinic","Login","Password reset emails not arriving","SMTP 535 authentication failed","SMTP app password had expired. Generated a new app password and updated it in appsettings."),
                ("Sunrise Medical","Lab Reports","Lab report PDF opens blank","Object reference not set in PdfRenderer.Render","Report template lost a font after a deployment. Re-installed the font package on the app server and cleared the template cache."),
                ("Lotus Health","Lab Reports","Lab results delayed by several hours","Message queue backlog over 5000 items","Queue consumer crashed on one malformed HL7 message. Moved the bad message to the dead-letter queue and restarted the consumer."),
                ("ABC Hospital","Pharmacy","Stock count shows negative quantities","Concurrency conflict in StockUpdate","Two dispenses updated stock at once. Wrapped the update in a serializable transaction with row locking."),
                ("Sunrise Medical","Patient Portal","Portal very slow during morning hours","CPU at 95% on web server, high GC time","Unbounded appointment list query. Added paging and caching for the dashboard; CPU dropped below 40%."),
                ("City Care Clinic","Patient Portal","Patients cannot download invoices","HTTP 500 FileNotFoundException","Invoice storage path changed after migration. Updated the storage path setting and re-linked old files."),
                ("Lotus Health","Appointment","Doctor calendar shows wrong time zone","DateTime offset mismatch UTC vs IST","Dates were stored in UTC but displayed without conversion. Added conversion to clinic time zone in the calendar API.")
            };

            foreach (var d in data)
            {
                await using var cmd = new SqlCommand(@"INSERT INTO Incidents (CustomerName, ModuleName, IssueDescription, ErrorDetails, Resolution, ResolutionStatus)
                    VALUES (@c,@m,@i,@e,@f,'Resolved')", conn);
                cmd.Parameters.AddWithValue("@c", d.c); cmd.Parameters.AddWithValue("@m", d.m); cmd.Parameters.AddWithValue("@i", d.i);
                cmd.Parameters.AddWithValue("@e", d.e); cmd.Parameters.AddWithValue("@f", d.f);
                await cmd.ExecuteNonQueryAsync();
            }

            await _hindsight.RetainManyAsync(data.Select(d =>
                $"Resolved incident in module {d.m} for {d.c}. Issue: {d.i}. Error: {d.e}. Confirmed fix that worked: {d.f}"));

            return Ok(new { message = $"Loaded {data.Length} past incidents. Memory finishes processing in about a minute." });
        }
    }
}
