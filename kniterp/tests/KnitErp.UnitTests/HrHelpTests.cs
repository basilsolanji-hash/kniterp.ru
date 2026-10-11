using KnitErp.Application.Workspace;

namespace KnitErp.UnitTests;

/// <summary>Справка по кадрам (D222): каждая страница модуля открывает свою статью, поиск находит её по словам пользователя.</summary>
public sealed class HrHelpTests
{
    [Theory]
    [InlineData("hr/orders", "hr-orders")]
    [InlineData("hr/orders/15", "hr-orders")]
    [InlineData("hr/staffing", "hr-orders")]
    [InlineData("hr/timesheet", "hr-timesheet")]
    [InlineData("hr/schedules", "hr-timesheet")]
    [InlineData("hr/payroll", "hr-payroll")]
    [InlineData("hr/piece-rates", "hr-payroll")]
    [InlineData("hr/settings", "hr-settings")]
    public void Hr_page_opens_its_help_article(string route, string expected) => Assert.Equal(expected, HelpCenter.ForRoute(route)?.Id);

    [Theory]
    [InlineData("как закрыть табель", "hr-timesheet")]
    [InlineData("перерасчёт заработка", "hr-payroll")]
    [InlineData("согласие на персональные данные", "hr-personal")]
    [InlineData("приказ сверх штата", "hr-orders")]
    public void Search_finds_hr_article(string query, string expected) => Assert.Contains(HelpCenter.Search(query, 5), a => a.Id == expected);
}
