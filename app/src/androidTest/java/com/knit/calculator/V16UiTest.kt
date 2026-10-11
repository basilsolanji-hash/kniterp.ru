package com.knit.calculator

import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onFirst
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextClearance
import androidx.compose.ui.test.performTextInput
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Rule
import org.junit.Test
import org.junit.rules.RuleChain
import org.junit.runner.RunWith

/** Версия 1.6.0: проверка ИНН, «Отменить» удаление позиции, «О версии». */
@RunWith(AndroidJUnit4::class)
class V16UiTest {
    private val compose = createAndroidComposeRule<MainActivity>()

    @get:Rule
    val rules: RuleChain = RuleChain.outerRule(ResetAppRule()).around(compose)

    @Test fun innCheckAndUndoRemove() {
        compose.onNodeWithContentDescription("Новый заказ — КП").performClick()
        val inn = compose.onNode(hasSetTextAction() and hasText("ИНН клиента"))
        inn.performTextInput("7701234567")
        compose.onNodeWithText("Неверный ИНН").assertExists()
        inn.performTextClearance()
        inn.performTextInput("9705239429")
        compose.onNodeWithText("Неверный ИНН").assertDoesNotExist()
        // ИНН организации — появляются КПП и юридический адрес для счёта и договора.
        compose.onAllNodes(hasText("КПП")).onFirst().performScrollTo().assertExists()
        Shots.take("60_inn_check", compose)

        compose.onNodeWithText("Добавить позицию").performScrollTo().performClick()
        compose.waitUntil(15_000) { compose.onAllNodes(hasText("Подвяз трикотажный")).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText("Подвяз трикотажный").performClick()
        compose.onNodeWithContentDescription("Удалить позицию").performClick()
        compose.onNodeWithText("Позиция удалена").assertExists()
        Shots.take("61_undo", compose)
        compose.onNodeWithText("Отменить").performClick()
        compose.onAllNodes(hasText("Подвяз трикотажный", substring = true)).onFirst().assertExists()
        compose.onNodeWithText("Позиция удалена").assertDoesNotExist()
    }

    @Test fun ownPinCode() {
        compose.onNodeWithContentDescription("Настройки").performClick()
        compose.onNodeWithText("Этот телефон").performClick()
        compose.onNodeWithText("Свой PIN-код (4 цифры)").performScrollTo()
        compose.onNodeWithContentDescription("PIN-код приложения").performClick()
        compose.onNodeWithText("Новый PIN-код").assertExists()
        // Простой PIN не принимается.
        "1234".forEach { compose.onNodeWithContentDescription(it.toString()).performClick() }
        compose.onNodeWithText("Слишком простой PIN: цифры подряд").assertExists()
        "2580".forEach { compose.onNodeWithContentDescription(it.toString()).performClick() }
        compose.onNodeWithText("Повторите PIN-код").assertExists()
        Shots.take("67_pin_setup", compose)
        "2580".forEach { compose.onNodeWithContentDescription(it.toString()).performClick() }
        compose.onNodeWithText("PIN-код сохранён").performScrollTo().assertExists()
    }

    @Test fun aboutScreen() {
        compose.openMenu("О версии")
        compose.onNodeWithText("Что нового").assertExists()
        compose.onNodeWithText("Google Таблица").assertExists()
        Shots.take("62_about", compose)
    }

    @Test fun settingsTabsAndQuoteSteps() {
        compose.onNodeWithContentDescription("Новый заказ — КП").performClick()
        compose.onNodeWithText("3 · Итог").assertExists()
        compose.onNodeWithText("Добавить позицию").performScrollTo().performClick()
        compose.waitUntil(15_000) { compose.onAllNodes(hasText("Подвяз трикотажный")).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText("Подвяз трикотажный").performClick()
        // Итог и «Сохранить» — внизу экрана, без прокрутки.
        compose.onNodeWithText("Итого").assertExists()
        compose.onNodeWithText("Сохранить").assertExists()
        compose.onNodeWithText("1 · Клиент").performClick()
        Shots.take("63_quote_steps", compose)

        compose.onNodeWithContentDescription("Реквизиты фабрики").performClick()
        compose.onNodeWithText("Подключить по QR-коду").assertExists()
        compose.onNodeWithText("Ежедневная сводка в 9:00").assertExists()
        compose.onNodeWithText("Реквизиты").performClick()
        compose.onNodeWithText("Подключить по QR-коду").assertDoesNotExist()
        Shots.take("64_settings_tabs", compose)
    }

    @Test fun templatesAndClientCard() {
        compose.onNodeWithContentDescription("Новый заказ — КП").performClick()
        compose.onNodeWithText("Добавить позицию").performScrollTo().performClick()
        compose.waitUntil(15_000) { compose.onAllNodes(hasText("Подвяз трикотажный")).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText("Подвяз трикотажный").performClick()
        // Клиента — после позиции: подсказка клиентов перекрывает кнопку «Добавить позицию».
        compose.onNodeWithText("Компания клиента").performScrollTo().performTextInput("ООО Ромашка")
        val qty = compose.onNode(hasSetTextAction() and hasText("Количество"))
        qty.performTextClearance()
        qty.performTextInput("600")

        // Шаблон: сохранить набор позиций и вернуть его одной кнопкой.
        compose.onNodeWithText("Сохранить как шаблон").performScrollTo().performClick()
        compose.onNode(hasSetTextAction() and hasText("Название шаблона")).performTextInput("Подвязы 600")
        compose.onAllNodes(hasText("Сохранить")).onFirst().assertExists()
        compose.onNode(hasText("Сохранить") and androidx.compose.ui.test.hasClickAction() and androidx.compose.ui.test.hasAnyAncestor(androidx.compose.ui.test.isDialog())).performClick()
        compose.onNodeWithText("Из шаблона…").performScrollTo().performClick()
        compose.onNodeWithText("Подвязы 600 · позиций: 1").performClick()
        compose.onAllNodes(hasText("2. Подвяз трикотажный", substring = true)).onFirst().assertExists()
        Shots.take("65_template", compose)

        // Карточка клиента из истории КП.
        compose.onNodeWithText("Сохранить КП").performScrollTo().performClick()
        Thread.sleep(3_000)
        compose.waitForIdle()
        compose.onNodeWithContentDescription("Заказы и КП").performClick()
        compose.waitUntil(10_000) { compose.onAllNodes(hasText("ООО Ромашка ›")).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText("ООО Ромашка ›").performClick()
        compose.waitUntil(10_000) { compose.onAllNodes(hasText("КП: 1 на", substring = true)).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText("Новый заказ — КП для клиента").assertExists()
        Shots.take("66_client_card", compose)
    }

    @Test fun costCalculatorMakesQuote() {
        // Без PIN директора все — директор: «Себестоимость» на главном.
        compose.openMenu("Себестоимость")
        compose.onNodeWithText("Выберите изделие (или считайте вручную)").performClick()
        compose.waitUntil(15_000) { compose.onAllNodes(hasText("Подвяз трикотажный")).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText("Подвяз трикотажный").performClick()
        compose.onNodeWithText("Себестоимость 1 шт").assertExists()
        compose.onNodeWithText("Без убытка от").assertExists()
        compose.onNodeWithText("Подставить рекомендованную цену").performClick()
        Shots.take("68_cost_calculator", compose)
        compose.onNodeWithText("Создать КП с этой ценой").performScrollTo().performClick()
        compose.waitUntil(5_000) { compose.onAllNodes(hasText("Итого")).fetchSemanticsNodes().isNotEmpty() }
        Shots.take("69_cost_quote", compose)
    }

    @Test fun commsTabsAndMailbox() {
        compose.openMenu("Связь")
        compose.onNodeWithText("Макс").assertExists()
        compose.onNodeWithText("Telegram").assertExists()
        compose.onNodeWithText("+ Почтовый ящик").performClick()
        compose.onNodeWithText("Яндекс Почта").performClick()
        compose.onNodeWithText("Добавить").performClick()
        compose.onNodeWithText("✉ Яндекс Почта").assertExists()
        Shots.take("70_comms", compose)
        // Вкладки: версия для ПК и удаление.
        compose.onNodeWithContentDescription("Вкладки").performClick()
        compose.onNodeWithContentDescription("Удалить «Яндекс Почта»").performClick()
        compose.onNodeWithText("Закрыть").performClick()
        compose.onNodeWithText("✉ Яндекс Почта").assertDoesNotExist()
    }

    @Test fun factoryServerScreens() {
        compose.openMenu("Сервер фабрики")
        compose.onNodeWithText("Первый вход директора").assertExists()
        Shots.take("71_staff_server", compose)
        compose.onNodeWithContentDescription("Назад").performClick()
        compose.openMenu("Этапы производства")
        compose.onNodeWithText("Подключитесь к серверу фабрики: меню ☰ → «Сервер фабрики».").assertExists()
    }

    @Test fun financeNeedsSheet() {
        // Без PIN директора все — директор: плитка «Финансы» видна.
        compose.openMenu("Финансы")
        compose.onNodeWithText("Платёжный календарь").assertExists()
        compose.onNodeWithText("Финансы работают с подключённой Google Таблицей.").assertExists()
    }
}
