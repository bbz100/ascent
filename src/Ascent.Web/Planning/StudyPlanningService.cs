using System.Text.Json;

using Ascent.Web.Infrastructure;

namespace Ascent.Web.Planning;



public sealed record QuestionWording(string Question);

public sealed record ModuleHeading(string Title);

public sealed class StudyPlanningService(IPlanningAi ai, IExpeditionStore store)

{

    public async Task<Expedition> StartAsync(string fileName, string document, CancellationToken ct, bool startNew=false, string? originalDocumentFile=null, Func<Expedition,bool,Task>? progress=null)

    {

        if (string.IsNullOrWhiteSpace(document) || document.Length > DocumentReader.MaxCharacters)

            throw new PlanningException("Provide a readable assessment brief within the document limit.");

        if (!startNew)

        {

            var matches = await store.FindByDocumentAsync(document, ct);

            if (matches.Count > 0) throw new DuplicateDocumentException(matches);

        }

        var expedition = new Expedition { FileName=Path.GetFileName(fileName),DocumentText=document,
            OriginalDocumentFile=originalDocumentFile,TitleExtractionAttempted=true };
        var savedTasks=false;
        var analysis=await AssessmentInterview.ReadAsync(ai,document,ct,async (facts,complete)=>{
            expedition.Analysis=facts with {InterviewWarning="Review the extracted tasks and dates to continue."};
            expedition.Title=facts.ModuleTitle;expedition.Questions=[];
            if(complete){
                if(startNew)await store.ReplaceDocumentAsync(expedition,ct);else await store.SaveAsync(expedition,ct);
                savedTasks=true;
            }
            if(progress is not null)await progress(expedition,complete);
        });
        expedition.Analysis=analysis;expedition.Questions=analysis.Questions;expedition.Title=analysis.ModuleTitle;
        if(startNew&&!savedTasks)await store.ReplaceDocumentAsync(expedition,ct);else await store.SaveAsync(expedition,ct);
        return expedition;

    }



    // Questions and heading are already generated from the complete brief. These

    // compatibility methods must not race background wording writes against answers.

    public Task<Expedition> PhraseQuestionAsync(Guid id, string questionId, CancellationToken ct) => store.GetAsync(id, ct);

    public Task<Expedition> ExtractHeadingAsync(Guid id, CancellationToken ct) => store.GetAsync(id, ct);



    public async Task<Expedition> AnswerAsync(Guid id, string questionId, string text, bool unknown, CancellationToken ct, QuestionResponse? details=null)

    {

        var expedition = await store.GetAsync(id, ct);

        if (expedition.Routes.Count > 0) throw new PlanningException("A route has already been generated.");

        var question = QuestionFlow.Pending(expedition).SingleOrDefault(q => q.Id == questionId)

            ?? throw new PlanningException("This question is not currently available.");

        if (!unknown && details is not null) text = AnswerFormatter.ValidateAndFormat(question, expedition.Analysis!, details);

        if (!unknown && details is null && question.InputType != "text")

            throw new PlanningException("Use the supplied fields to answer this question.");

        if (!unknown && string.IsNullOrWhiteSpace(text)) throw new PlanningException("Enter an answer or choose ‘I don’t know yet’.");

        if (text.Length > 4000) throw new PlanningException("Keep each answer under 4,000 characters.");

        expedition.Answers.Add(new(question.Id, question.GapKey, unknown?"I don't know yet":text.Trim(), unknown, unknown?null:details));

        await store.SaveAsync(expedition, ct);

        return expedition;

    }



    public Task<Expedition> FollowUpAsync(Guid id, CancellationToken ct) => store.GetAsync(id, ct);



    public async Task<Expedition> SaveDatesAsync(Guid id, DateReviewSubmission submission, CancellationToken ct)

    {

        var expedition = await store.GetAsync(id, ct);

        if (expedition.Routes.Count > 0) throw new PlanningException("Dates can be edited before generating your routes.");

        var deadlines = expedition.Analysis!.Deadlines;

        if (submission.Dates is null || submission.Dates.Count != deadlines.Count ||

            submission.Dates.Select(d => d.DeadlineId).Distinct().Count() != deadlines.Count ||

            submission.Dates.Any(d => !deadlines.Any(source => source.Id == d.DeadlineId)))

            throw new PlanningException("The dates do not match this interview. Reopen it and try again.");

        var reviewed = new List<DeadlineReview>();

        foreach (var date in submission.Dates)

        {

            var source = deadlines.Single(d => d.Id == date.DeadlineId);

            if (date.UseForPlan && (source.Purpose != "submission" || date.Date is null))

                throw new PlanningException("Only a submission date with a chosen day can be used in your plan.");

            string? time = null;

            if (!string.IsNullOrWhiteSpace(date.Time))

            {

                if (!CalendarNotation.TryTime(date.Time, out var parsed)) throw new PlanningException("Choose a valid time or leave it blank.");

                time = parsed.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

            }

            reviewed.Add(date with { Time = time });

        }

        if (expedition.Analysis.InterviewWarning is not null && !submission.TasksReviewed)

            throw new PlanningException("Review the extracted tasks before continuing manually.");

        expedition.DateReviews = reviewed;

        expedition.DatesReviewed = true;

        expedition.TasksReviewed = submission.TasksReviewed;

        await store.SaveAsync(expedition, ct);

        return expedition;

    }



    public async Task<Expedition> GenerateAsync(Guid id, CancellationToken ct)

    {

        var expedition = await store.GetAsync(id, ct);

        if (expedition.Routes.Count > 0) return expedition;

        if (expedition.Analysis?.SchemaVersion != 2)

            throw new PlanningException("This project uses the earlier planning engine. Its saved work is preserved; analyse the brief as a new project to use model-generated questions.");

        if (QuestionFlow.Pending(expedition).Count > 0)

            throw new PlanningException("Answer the remaining questions or choose ‘I don’t know yet’ first.");

        if (expedition.Analysis.InterviewWarning is not null && (!expedition.DatesReviewed || !expedition.TasksReviewed))

            throw new PlanningException("Review and save the tasks and dates before generating your routes.");

        var included = QuestionFlow.IncludedRequirements(expedition);

        var context = JsonSerializer.Serialize(new {

            analysis=expedition.Analysis, answers=expedition.Answers,

            includedRequirementIds=included.Select(r=>r.Id),

            unresolvedRequirementIds=included.Where(r=>QuestionFlow.Applies(r.AppliesWhen,expedition) is null).Select(r=>r.Id),

            applicableDeadlines=QuestionFlow.SelectedDeadlines(expedition),

            studentConfirmedDates=expedition.DatesReviewed ? expedition.DateReviews : []

        }, CompatiblePlanningAi.Json);

        var planReferences = new Dictionary<string,List<string>> {

                ["result-items-item-requirementid"] = included.Select(r=>r.Id).ToList(),

                ["result-items-item-deadlineid-value"] = QuestionFlow.SelectedDeadlines(expedition).Where(d=>d.Purpose=="submission" && d.DatePrecision=="day").Select(d=>d.Id).ToList()

            };

        // With no exact dates and no dates entered by the student, date output must be null.

        // This also prevents an optional date-stage failure resurfacing as a route error.

        var studentSuppliedDate = expedition.Answers.Any(a=>!a.Unknown && a.Data is {} data &&

            (data.Date is not null || data.Workshops.Any(w=>w.Date is not null) || data.Values.Any(v=>v.Date is not null)));

        if (planReferences["result-items-item-deadlineid-value"].Count==0 && !studentSuppliedDate)

        {

            planReferences["result-items-item-duedate-value"] = [];

            planReferences["result-items-item-dateevidence-value"] = [];

        }

        WorkPlan plan;
        using var routeTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        routeTimeout.CancelAfter(TimeSpan.FromSeconds(60));

        try

        {

        plan = await ai.GenerateAsync<WorkPlan>(PlanningPrompts.Plan, context,

            result=>{

                // Applicability is already known from the saved answers. Do not ask

                // the model to decide whether an unanswered condition is resolved.

                foreach (var item in result.Items ?? [])

                {

                    var requirement=included.SingleOrDefault(r=>r.Id==item.RequirementId);

                    if (requirement is not null && QuestionFlow.Applies(requirement.AppliesWhen,expedition) is null)

                    {

                        item.Placeholder=true;

                        item.DueDate=null;item.DeadlineId=null;item.DateEvidence=null;

                    }

                }

                PlanValidator.WorkPlan(result, expedition);

            }, routeTimeout.Token, references:planReferences);

        }

        catch (Exception ex) when (!ct.IsCancellationRequested && ex is PlanningException or JsonException or HttpRequestException or OperationCanceledException)

        {

            // A conservative draft uses only the already validated extracted tasks.

            // Do not invent dates, effort, eligibility or dependencies to make AI output pass.

            var items = included.Where(r=>r.Kind!="constraint").SelectMany(r=>

                Enumerable.Range(1,r.Count).Select(occurrence=>new WorkItem {

                    Id=$"draft-{r.Id}-{occurrence}",RequirementId=r.Id,Occurrence=occurrence,

                    Title=r.Count==1?r.Title:$"{r.Title} - {occurrence} of {r.Count}",

                    Description=r.Description,KeepSeparate=r.Count>1 || r.Kind=="workshop",

                    Placeholder=QuestionFlow.Applies(r.AppliesWhen,expedition) is null

                })).ToList();

            plan=new(items);

            PlanValidator.WorkPlan(plan,expedition);

            expedition.Analysis=expedition.Analysis with {

                Warnings=expedition.Analysis.Warnings.Append("A simple draft route has been built from your extracted tasks. Review the order before using it; dates and effort remain unconfirmed.").ToList()

            };

        }



        expedition.WorkItems=plan.Items;

        expedition.Routes=RouteBuilder.Build(plan.Items,included);

        await store.SaveAsync(expedition,ct);

        return expedition;

    }

    public async Task<Expedition> ApproveAsync(Guid id, string routeId, CancellationToken ct)

    {

        var e = await store.GetAsync(id, ct);

        if (e.ApprovedRouteId is not null) throw new PlanningException("A route has already been approved.");

        if (!e.Routes.Any(r => r.Id == routeId)) throw new PlanningException("This route wasn't found.");

        e.ApprovedRouteId = routeId; await store.SaveAsync(e, ct); return e;

    }

    public async Task<Expedition> CompleteAsync(Guid id, string milestoneId, string notes, CancellationToken ct)

    {

        var e = await store.GetAsync(id, ct);

        var route = e.Routes.SingleOrDefault(r => r.Id == e.ApprovedRouteId) ?? throw new PlanningException("Approve a route first.");

        var m = route.Milestones.SingleOrDefault(m => m.Id == milestoneId) ?? throw new PlanningException("This checkpoint wasn't found.");

        if (!RouteBuilder.Available(m, route)) throw new PlanningException("This checkpoint isn't available yet.");

        if (notes.Length > 4000) throw new PlanningException("Keep notes under 4,000 characters.");

        m.Complete = true; m.Notes = notes; await store.SaveAsync(e, ct); return e;

    }

}

