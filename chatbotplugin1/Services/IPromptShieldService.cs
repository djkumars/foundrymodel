namespace foundry_chatbot_api.Services;

public interface IPromptShieldService
{
    bool IsConfigured { get; }

    Task<bool> IsAttackAsync(string prompt, CancellationToken cancellationToken);
}
