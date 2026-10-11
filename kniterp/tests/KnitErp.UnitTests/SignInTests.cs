using System.Text;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

public sealed class TotpTests
{
    // RFC 6238, приложение B: секрет «12345678901234567890», SHA-1, 8 цифр.
    private static readonly string RfcKey = Base32.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_rfc6238_test_vectors(long unixSeconds, string expected)
    {
        Assert.Equal(expected, Totp.Compute(RfcKey, unixSeconds / Totp.StepSeconds, digits: 8));
    }

    [Fact]
    public void Accepts_code_one_step_off_and_rejects_two_steps_off()
    {
        var key = Totp.NewKey();
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var step = Totp.StepAt(now);

        Assert.Equal(step, Totp.FindMatchingStep(key, Totp.Compute(key, step), now));
        Assert.Equal(step - 1, Totp.FindMatchingStep(key, Totp.Compute(key, step - 1), now));
        Assert.Equal(step + 1, Totp.FindMatchingStep(key, Totp.Compute(key, step + 1), now));
        Assert.Null(Totp.FindMatchingStep(key, Totp.Compute(key, step - 2), now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("12345a")]
    [InlineData("1234567")]
    public void Malformed_code_is_rejected(string? code)
    {
        Assert.Null(Totp.FindMatchingStep(Totp.NewKey(), code, DateTime.UtcNow));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_rfc4648_without_padding(string plain, string encoded)
    {
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(plain)));
        Assert.Equal(plain, Encoding.ASCII.GetString(Base32.Decode(encoded)));
    }

    [Fact]
    public void Provisioning_uri_names_issuer_and_account()
    {
        var uri = Totp.ProvisioningUri("knitERP", "owner@test.local", "ABCDEF");
        Assert.Equal("otpauth://totp/knitERP:owner%40test.local?secret=ABCDEF&issuer=knitERP&digits=6&period=30", uri);
    }
}

public sealed class UserSignInRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fifth_failed_attempt_locks_for_fifteen_minutes()
    {
        var user = ActiveUser();
        for (var i = 1; i < SignInPolicy.MaxFailedAttempts; i++)
        {
            Assert.False(user.RegisterFailedSignIn(Now));
            Assert.False(user.IsLockedOut(Now));
        }

        Assert.True(user.RegisterFailedSignIn(Now));
        Assert.True(user.IsLockedOut(Now.AddMinutes(14)));
        Assert.False(user.IsLockedOut(Now.AddMinutes(15)));
    }

    [Fact]
    public void Successful_sign_in_resets_failure_counter()
    {
        var user = ActiveUser();
        for (var i = 1; i < SignInPolicy.MaxFailedAttempts; i++)
        {
            user.RegisterFailedSignIn(Now);
        }

        user.RegisterSuccessfulSignIn();
        Assert.False(user.RegisterFailedSignIn(Now));
        Assert.Equal(1, user.FailedSignInCount);
    }

    [Fact]
    public void Invitation_link_works_once_and_activates_user()
    {
        var user = UserAccount.Invite("new@test.local", "Новый", Now);
        var token = user.IssueSetupToken(Now);
        var stamp = user.SecurityStamp;

        user.CompleteSetup(token, "hash", Now.AddHours(71));

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.True(user.HasPassword);
        Assert.Null(user.SetupTokenHash);
        Assert.NotEqual(stamp, user.SecurityStamp);
        var again = Assert.Throws<BusinessRuleException>(() => user.CompleteSetup(token, "hash2", Now.AddHours(71)));
        Assert.Equal("auth.invitation.invalid", again.Code);
    }

    [Fact]
    public void Invitation_expires_after_72_hours()
    {
        var user = UserAccount.Invite("late@test.local", "Опоздавший", Now);
        var token = user.IssueSetupToken(Now);

        var ex = Assert.Throws<BusinessRuleException>(() => user.CompleteSetup(token, "hash", Now.AddHours(72)));
        Assert.Equal("auth.invitation.expired", ex.Code);
        Assert.Equal(UserStatus.Invited, user.Status);
    }

    [Fact]
    public void Reissued_invitation_invalidates_previous_link()
    {
        var user = UserAccount.Invite("again@test.local", "Повтор", Now);
        var first = user.IssueSetupToken(Now);
        var second = user.IssueSetupToken(Now.AddHours(80));

        Assert.Equal("auth.invitation.invalid",
            Assert.Throws<BusinessRuleException>(() => user.CompleteSetup(first, "hash", Now.AddHours(81))).Code);
        user.CompleteSetup(second, "hash", Now.AddHours(81));
        Assert.True(user.HasPassword);
    }

    [Fact]
    public void User_with_password_needs_no_invitation()
    {
        var user = ActiveUser();
        Assert.Equal("auth.invitation.not_needed", Assert.Throws<BusinessRuleException>(() => user.IssueSetupToken(Now)).Code);
    }

    [Fact]
    public void Two_factor_code_cannot_be_replayed()
    {
        var user = ActiveUser();
        var key = user.BeginAuthenticatorSetup();
        var code = Totp.Compute(key, Totp.StepAt(Now));

        Assert.True(user.ConfirmTwoFactor(code, Now));
        Assert.True(user.TwoFactorEnabled);
        Assert.False(user.VerifyTotp(code, Now.AddSeconds(10)));
        Assert.True(user.VerifyTotp(Totp.Compute(key, Totp.StepAt(Now) + 1), Now.AddSeconds(30)));
    }

    [Fact]
    public void Wrong_code_does_not_enable_two_factor_and_reset_requires_new_setup()
    {
        var user = ActiveUser();
        var key = user.BeginAuthenticatorSetup();
        Assert.False(user.ConfirmTwoFactor("000000" == Totp.Compute(key, Totp.StepAt(Now)) ? "111111" : "000000", Now));
        Assert.False(user.TwoFactorEnabled);
        Assert.Equal(key, user.BeginAuthenticatorSetup());

        Assert.True(user.ConfirmTwoFactor(Totp.Compute(key, Totp.StepAt(Now)), Now));
        user.ResetTwoFactor();
        Assert.False(user.TwoFactorEnabled);
        Assert.Null(user.AuthenticatorKey);
        Assert.NotEqual(key, user.BeginAuthenticatorSetup());
    }

    [Theory]
    [InlineData("short", "auth.password.too_short")]
    [InlineData("aaaaaaaaaaaa", "auth.password.too_simple")]
    [InlineData("user@test.local", "auth.password.equals_email")]
    public void Weak_password_is_rejected(string password, string code)
    {
        Assert.Equal(code, Assert.Throws<BusinessRuleException>(() => SignInPolicy.EnsurePasswordAcceptable(password, "user@test.local")).Code);
    }

    [Fact]
    public void Passphrase_is_accepted()
    {
        SignInPolicy.EnsurePasswordAcceptable("вязальная машина шумит", "user@test.local");
    }

    [Fact]
    public void Only_owner_and_administrator_require_two_factor()
    {
        Assert.All(SystemRoles.Ordered, code =>
            Assert.Equal(code is SystemRoles.Owner or SystemRoles.Administrator, SignInPolicy.RequiresTwoFactor(code)));
    }

    [Theory]
    [InlineData("qwertyuiop")]
    [InlineData("Password123")]
    [InlineData("1q2w3e4r5t")]
    [InlineData("abcdefghijk")]
    [InlineData("9876543210")]
    [InlineData("ivan.petrov2026!")]
    public void D115_common_sequential_and_email_based_passwords_are_rejected(string password)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => SignInPolicy.EnsurePasswordAcceptable(password, "ivan.petrov@test.local"));
        Assert.Equal("auth.password.common", ex.Code);
    }

    [Theory]
    [InlineData("вязальная машина у окна")]
    [InlineData("Kx7#mPq2vL9z")]
    public void D115_long_unusual_passwords_are_accepted(string password) =>
        SignInPolicy.EnsurePasswordAcceptable(password, "ivan.petrov@test.local");

    [Fact]
    public void D115_changing_password_resets_failures_and_closes_sessions()
    {
        var user = ActiveUser();
        user.RegisterFailedSignIn(Now);
        var stamp = user.SecurityStamp;
        user.ChangePassword("new-hash");
        Assert.Equal("new-hash", user.PasswordHash);
        Assert.Equal(0, user.FailedSignInCount);
        Assert.NotEqual(stamp, user.SecurityStamp);
    }

    private static UserAccount ActiveUser()
    {
        var user = UserAccount.Invite("user@test.local", "Пользователь", Now);
        user.CompleteSetup(user.IssueSetupToken(Now), "hash", Now);
        return user;
    }
}
