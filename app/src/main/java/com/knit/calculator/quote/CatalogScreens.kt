package com.knit.calculator.quote

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import kotlinx.coroutines.launch
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.knit.calculator.R
import com.knit.calculator.core.Product
import com.knit.calculator.core.QuoteCalculator
import com.knit.calculator.ui.components.ActionButton
import com.knit.calculator.ui.components.KnitField
import com.knit.calculator.ui.components.KnitIconButton
import com.knit.calculator.ui.components.ScreenTopBar
import com.knit.calculator.ui.components.SectionTitle
import com.knit.calculator.ui.theme.LocalKnitColors

/** Общая обёртка экрана: фон, отступы под системные панели и клавиатуру, прокрутка. */
@Composable
internal fun FormScreen(
    title: String,
    onBack: () -> Unit,
    actions: @Composable androidx.compose.foundation.layout.RowScope.() -> Unit = {},
    content: @Composable ColumnScope.() -> Unit,
) {
    val colors = LocalKnitColors.current
    BackHandler(onBack = onBack)
    Column(
        Modifier
            .fillMaxSize()
            .background(colors.background)
            .safeDrawingPadding()
            .imePadding(),
    ) {
        ScreenTopBar(title, onBack, actions)
        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            content()
            Spacer(Modifier.height(24.dp))
        }
    }
}

@Composable
private fun AddButton(label: Int, onClick: () -> Unit) {
    val colors = LocalKnitColors.current
    OutlinedButton(onClick = onClick) {
        Icon(painterResource(R.drawable.ic_add), null, Modifier.size(18.dp), tint = colors.textPrimary)
        Spacer(Modifier.width(6.dp))
        Text(stringResource(label), color = colors.textPrimary)
    }
}

// ============================ Ассортимент ============================

@Composable
fun CatalogScreen(viewModel: QuoteViewModel, onBack: () -> Unit, onEdit: (Long?) -> Unit) {
    val catalog by viewModel.catalog.collectAsStateWithLifecycle()
    val sync by viewModel.sync.collectAsStateWithLifecycle()
    val colors = LocalKnitColors.current
    val context = LocalContext.current
    var confirmReset by rememberSaveable { mutableStateOf(false) }
    var showQr by rememberSaveable { mutableStateOf(false) }
    val settings by viewModel.settings.collectAsStateWithLifecycle()
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    val fromSheet = sync.connected

    val msProducts by viewModel.msProducts.collectAsStateWithLifecycle()
    FormScreen(stringResource(R.string.catalog_title), onBack) {
        if (sync.msEnabled) {
            val updated = sync.msLoadedAt?.let { java.text.SimpleDateFormat("dd.MM HH:mm", java.util.Locale.getDefault()).format(java.util.Date(it)) } ?: "—"
            Text(stringResource(R.string.catalog_ms_info, msProducts.size, sync.msStore.ifBlank { "—" }, updated), color = colors.textSecondary, fontSize = 14.sp)
            sync.msError?.let { Text(stringResource(R.string.ms_error, it), color = colors.textPrimary, fontSize = 14.sp) }
            ActionButton(R.string.ms_refresh, R.drawable.ic_arrow_down, primary = false, Modifier.fillMaxWidth()) { viewModel.refreshMs(force = true) }
            if (sync.msLoading) Text(stringResource(R.string.sync_loading), color = colors.textSecondary, fontSize = 14.sp)
        }
        // Прайс-лист для клиента и QR-код на каталог сайта.
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
            ActionButton(R.string.price_list_pdf, R.drawable.ic_doc, primary = false, Modifier.weight(1f)) {
                scope.launch(kotlinx.coroutines.Dispatchers.Main) {
                    val file = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                        runCatching { DocPdf.priceList(context, settings, catalog) }.getOrNull()
                    }
                    if (file == null) {
                        android.widget.Toast.makeText(context, R.string.yarn_pdf_error, android.widget.Toast.LENGTH_LONG).show()
                    } else {
                        viewModel.uploadDocument(file, null)
                        com.knit.calculator.report.ReportSharing.share(
                            context, file, context.getString(R.string.price_list_subject, settings.brand),
                            context.getString(R.string.price_list_text, settings.shopUrl, settings.signature, settings.phone, settings.email),
                        )
                    }
                }
            }
            ActionButton(R.string.qr_title, R.drawable.ic_qr, primary = false, Modifier.weight(1f)) { showQr = true }
        }
        if (fromSheet) {
            Text(stringResource(R.string.catalog_from_sheet), color = colors.textSecondary, fontSize = 14.sp)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                ActionButton(R.string.sync_open_sheet, R.drawable.ic_list, primary = true, Modifier.weight(1f)) {
                    openUrl(context, sync.sheetUrl)
                }
                ActionButton(R.string.sync_refresh, R.drawable.ic_arrow_down, primary = false, Modifier.weight(1f)) { viewModel.refresh() }
            }
            if (sync.loading) Text(stringResource(R.string.sync_loading), color = colors.textSecondary, fontSize = 14.sp)
            sync.error?.let { Text(it, color = colors.textPrimary, fontSize = 14.sp) }
            sync.warnings.forEach { Text("• $it", color = colors.textSecondary, fontSize = 13.sp) }
        } else {
            Text(stringResource(R.string.catalog_hint), color = colors.textSecondary, fontSize = 14.sp)
            AddButton(R.string.catalog_add) { onEdit(null) }
        }
        catalog.forEachIndexed { index, product ->
            CatalogItem(
                product = product,
                editable = !fromSheet,
                canMoveUp = index > 0,
                canMoveDown = index < catalog.lastIndex,
                onClick = { onEdit(product.id) },
                onMoveUp = { viewModel.moveProduct(product.id, -1) },
                onMoveDown = { viewModel.moveProduct(product.id, 1) },
            )
        }
        if (!fromSheet) {
            TextButton(onClick = { confirmReset = true }, modifier = Modifier.align(Alignment.CenterHorizontally)) {
                Text(stringResource(R.string.catalog_reset), color = colors.textSecondary)
            }
        }
    }

    if (showQr) QrDialog(settings.shopUrl) { showQr = false }

    if (confirmReset) {
        ConfirmDialog(
            title = stringResource(R.string.catalog_reset_title),
            text = stringResource(R.string.catalog_reset_text),
            confirm = stringResource(R.string.reset),
            onConfirm = { confirmReset = false; viewModel.resetCatalog() },
            onDismiss = { confirmReset = false },
        )
    }
}

@Composable
private fun CatalogItem(
    product: Product,
    editable: Boolean,
    canMoveUp: Boolean,
    canMoveDown: Boolean,
    onClick: () -> Unit,
    onMoveUp: () -> Unit,
    onMoveDown: () -> Unit,
) {
    val colors = LocalKnitColors.current
    Surface(onClick = onClick, enabled = editable, shape = RoundedCornerShape(20.dp), color = colors.panel, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(start = 16.dp, top = 12.dp, bottom = 12.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(product.name, color = colors.textPrimary, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
                Text(
                    stringResource(
                        R.string.catalog_item_summary,
                        QuoteCalculator.formatMoney(product.basePrice),
                        product.unit,
                        QuoteCalculator.formatQuantity(product.minOrder),
                    ),
                    color = colors.textSecondary,
                    fontSize = 14.sp,
                )
                if (product.options.isNotEmpty()) {
                    Text(product.options.joinToString(" · ") { "${it.name} (${it.choices.size})" }, color = colors.textSecondary, fontSize = 13.sp)
                }
                if (product.tiers.isNotEmpty()) {
                    Text(
                        stringResource(R.string.catalog_tiers, product.tiers.size),
                        color = colors.textSecondary,
                        fontSize = 13.sp,
                    )
                }
            }
            if (editable) {
                KnitIconButton(R.drawable.ic_arrow_up, stringResource(R.string.catalog_move_up), onMoveUp, enabled = canMoveUp)
                KnitIconButton(R.drawable.ic_arrow_down, stringResource(R.string.catalog_move_down), onMoveDown, enabled = canMoveDown)
            }
        }
    }
}

// ============================ Редактор изделия ============================

@Composable
fun ProductEditorScreen(viewModel: QuoteViewModel, onDone: () -> Unit) {
    val editing by viewModel.editing.collectAsStateWithLifecycle()
    val product = editing
    // После перезапуска процесса редактор пуст — возвращаемся к списку.
    LaunchedEffect(product == null) { if (product == null) onDone() }
    if (product == null) return

    val colors = LocalKnitColors.current
    val isNew = viewModel.isNewProduct(product.id)
    var showError by rememberSaveable { mutableStateOf(false) }
    var confirmDelete by rememberSaveable { mutableStateOf(false) }
    val close = {
        viewModel.cancelEditing()
        onDone()
    }

    FormScreen(stringResource(if (isNew) R.string.product_new else R.string.product_edit), onBack = close) {
        SectionTitle(R.string.product_section_main)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            KnitField(product.code, { v -> viewModel.edit { it.copy(code = v) } }, R.string.product_code, text = true, maxLength = 20, modifier = Modifier.weight(0.6f))
            KnitField(product.name, { v -> viewModel.edit { it.copy(name = v) } }, R.string.product_name, text = true, maxLength = 80, modifier = Modifier.weight(1.4f))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            KnitField(product.unit, { v -> viewModel.edit { it.copy(unit = v) } }, R.string.product_unit, text = true, maxLength = 10, modifier = Modifier.weight(1f))
            KnitField(product.basePrice, { v -> viewModel.edit { it.copy(basePrice = v) } }, R.string.product_base_price, modifier = Modifier.weight(1f))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            KnitField(product.minOrder, { v -> viewModel.edit { it.copy(minOrder = v) } }, R.string.product_min_order, suffix = product.unit, modifier = Modifier.weight(1f))
            KnitField(product.setupFee, { v -> viewModel.edit { it.copy(setupFee = v) } }, R.string.product_setup_fee, modifier = Modifier.weight(1f))
        }
        KnitField(product.rounding, { v -> viewModel.edit { it.copy(rounding = v) } }, R.string.product_rounding)

        SectionTitle(R.string.product_section_options)
        Text(stringResource(R.string.product_options_hint), color = colors.textSecondary, fontSize = 14.sp)
        product.groups.forEach { group ->
            GroupEditor(
                group = group,
                onChange = { updated -> viewModel.edit { p -> p.copy(groups = p.groups.map { if (it.id == group.id) updated else it }) } },
                onRemove = { viewModel.edit { p -> p.copy(groups = p.groups.filterNot { it.id == group.id }) } },
            )
        }
        AddButton(R.string.product_add_group) { viewModel.edit { it.copy(groups = it.groups + EditableGroup()) } }

        SectionTitle(R.string.product_section_tiers)
        Text(stringResource(R.string.product_tiers_hint), color = colors.textSecondary, fontSize = 14.sp)
        product.tiers.forEach { tier ->
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                fun update(t: EditableTier) = viewModel.edit { p -> p.copy(tiers = p.tiers.map { if (it.id == tier.id) t else it }) }
                KnitField(tier.fromQuantity, { update(tier.copy(fromQuantity = it)) }, R.string.product_tier_from, suffix = product.unit, modifier = Modifier.weight(1f))
                KnitField(tier.factor, { update(tier.copy(factor = it)) }, R.string.product_tier_factor, text = true, keyboardType = KeyboardType.Text, modifier = Modifier.weight(1f))
                KnitIconButton(R.drawable.ic_close, stringResource(R.string.product_remove_tier), {
                    viewModel.edit { p -> p.copy(tiers = p.tiers.filterNot { it.id == tier.id }) }
                })
            }
        }
        AddButton(R.string.product_add_tier) { viewModel.edit { it.copy(tiers = it.tiers + EditableTier()) } }

        if (showError) {
            Text(stringResource(R.string.product_invalid), color = colors.textPrimary, fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
        }
        Spacer(Modifier.height(4.dp))
        ActionButton(R.string.product_save, R.drawable.ic_edit, primary = true, Modifier.fillMaxWidth()) {
            if (viewModel.saveEditing()) onDone() else showError = true
        }
        if (!isNew) {
            TextButton(onClick = { confirmDelete = true }, modifier = Modifier.align(Alignment.CenterHorizontally)) {
                Text(stringResource(R.string.product_delete), color = colors.textSecondary)
            }
        }
    }

    if (confirmDelete) {
        ConfirmDialog(
            title = stringResource(R.string.product_delete_title),
            text = stringResource(R.string.product_delete_text),
            confirm = stringResource(R.string.delete),
            onConfirm = {
                confirmDelete = false
                viewModel.deleteProduct(product.id)
                close()
            },
            onDismiss = { confirmDelete = false },
        )
    }
}

@Composable
private fun GroupEditor(group: EditableGroup, onChange: (EditableGroup) -> Unit, onRemove: () -> Unit) {
    val colors = LocalKnitColors.current
    Surface(shape = RoundedCornerShape(20.dp), color = colors.panel, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(start = 12.dp, end = 4.dp, top = 8.dp, bottom = 8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                KnitField(group.name, { onChange(group.copy(name = it)) }, R.string.product_group_name, text = true, modifier = Modifier.weight(1f))
                KnitIconButton(R.drawable.ic_delete, stringResource(R.string.product_remove_group), onRemove)
            }
            group.choices.forEach { choice ->
                fun update(c: EditableChoice) = onChange(group.copy(choices = group.choices.map { if (it.id == choice.id) c else it }))
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    KnitField(choice.name, { update(choice.copy(name = it)) }, R.string.product_choice_name, text = true, modifier = Modifier.weight(1.2f))
                    KnitField(choice.factor, { update(choice.copy(factor = it)) }, R.string.product_choice_factor, text = true, keyboardType = KeyboardType.Text, modifier = Modifier.weight(0.9f))
                    KnitField(choice.priceAdd, { update(choice.copy(priceAdd = it)) }, R.string.product_choice_add, modifier = Modifier.weight(0.8f))
                    KnitIconButton(R.drawable.ic_close, stringResource(R.string.product_remove_choice), {
                        onChange(group.copy(choices = group.choices.filterNot { it.id == choice.id }))
                    })
                }
            }
            TextButton(onClick = { onChange(group.copy(choices = group.choices + EditableChoice())) }) {
                Icon(painterResource(R.drawable.ic_add), null, Modifier.size(18.dp), tint = colors.textPrimary)
                Spacer(Modifier.width(6.dp))
                Text(stringResource(R.string.product_add_choice), color = colors.textPrimary)
            }
        }
    }
}

// ============================ Реквизиты ============================

@Composable
fun CompanyScreen(viewModel: QuoteViewModel, onBack: () -> Unit) {
    val s by viewModel.settings.collectAsStateWithLifecycle()
    val config by viewModel.syncConfig.collectAsStateWithLifecycle()
    val sync by viewModel.sync.collectAsStateWithLifecycle()
    val colors = LocalKnitColors.current
    val context = LocalContext.current
    val director by viewModel.director.collectAsStateWithLifecycle()
    // Без таблицы реквизиты правятся здесь; менеджер (при заданном PIN) их не меняет.
    val editable = !config.enabled && director
    fun set(transform: (CompanySettings) -> CompanySettings) = viewModel.updateSettings(transform)

    var url by rememberSaveable { mutableStateOf(config.url) }
    var key by rememberSaveable { mutableStateOf(config.key) }
    var manager by rememberSaveable { mutableStateOf(config.manager) }
    var message by rememberSaveable { mutableStateOf<String?>(null) }
    val connectedMessage = stringResource(R.string.sync_connected)

    // Настройки разделами: на экране — только выбранный раздел.
    var section by rememberSaveable { mutableStateOf(0) }
    val sections = listOf(
        R.string.settings_tab_connect, R.string.settings_tab_phone, R.string.settings_tab_requisites, R.string.settings_tab_terms,
        R.string.settings_tab_legal, R.string.settings_tab_role,
    )
    FormScreen(stringResource(R.string.settings_title), onBack) {
        androidx.compose.foundation.lazy.LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            items(sections.size) { i ->
                androidx.compose.material3.FilterChip(
                    selected = section == i,
                    onClick = { section = i },
                    label = { Text(stringResource(sections[i])) },
                    colors = androidx.compose.material3.FilterChipDefaults.filterChipColors(
                        selectedContainerColor = colors.equalsKey,
                        selectedLabelColor = colors.equalsKeyText,
                        labelColor = colors.textPrimary,
                    ),
                )
            }
        }
        when (section) {
            0 -> {
            SectionTitle(R.string.sync_section)
            Text(stringResource(if (config.enabled) R.string.sync_hint_connected else R.string.sync_hint), color = colors.textSecondary, fontSize = 14.sp)
            KnitField(url, { url = it.trim() }, R.string.sync_url, text = true, keyboardType = KeyboardType.Uri, maxLength = 300, enabled = !config.enabled)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                KnitField(key, { key = it.trim() }, R.string.sync_key, text = true, keyboardType = KeyboardType.Password, maxLength = 64, enabled = !config.enabled, modifier = Modifier.weight(1f))
                KnitField(manager, { manager = it; if (config.enabled) viewModel.updateManager(it) }, R.string.sync_manager, text = true, maxLength = 60, modifier = Modifier.weight(1f))
            }
            if (config.enabled) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                    ActionButton(R.string.sync_open_sheet, R.drawable.ic_list, primary = true, Modifier.weight(1f)) { openUrl(context, sync.sheetUrl) }
                    ActionButton(R.string.sync_disconnect, R.drawable.ic_close, primary = false, Modifier.weight(1f)) {
                        viewModel.disconnect()
                        message = null
                    }
                }
            } else {
                ActionButton(R.string.sync_connect, R.drawable.ic_share, primary = true, Modifier.fillMaxWidth()) {
                    message = null
                    viewModel.connect(url, key, manager) { error -> message = error ?: connectedMessage }
                }
                // QR-код из таблицы (меню «Фабрика KS → QR для подключения телефона»): адрес, ключ и имя — без ручного ввода.
                ActionButton(R.string.sync_qr, R.drawable.ic_qr, primary = false, Modifier.fillMaxWidth()) {
                    message = null
                    QrConnect.scan(context, onResult = { qr ->
                        url = qr.url
                        key = qr.key
                        if (qr.name.isNotBlank()) manager = qr.name
                        viewModel.connect(qr.url, qr.key, manager) { error -> message = error ?: connectedMessage }
                    }, onError = { message = it })
                }
            }
            if (sync.loading) Text(stringResource(R.string.sync_loading), color = colors.textSecondary, fontSize = 14.sp)
            // Ежедневная сводка: включение и выключение.
            var digest by rememberSaveable { mutableStateOf(QuoteStore(context).digestEnabled) }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.digest_setting), color = colors.textPrimary, fontSize = 16.sp)
                    Text(stringResource(R.string.digest_setting_hint), color = colors.textSecondary, fontSize = 13.sp)
                }
                Switch(
                    checked = digest,
                    onCheckedChange = { v ->
                        digest = v
                        QuoteStore(context).digestEnabled = v
                        DailyDigest.schedule(context, v)
                    },
                    colors = SwitchDefaults.colors(checkedTrackColor = colors.equalsKey, checkedThumbColor = colors.equalsKeyText),
                )
            }
            message?.let { Text(it, color = colors.textPrimary, fontWeight = FontWeight.SemiBold, fontSize = 14.sp) }
            }
            1 -> {
            SectionTitle(R.string.settings_tab_phone)
            Text(stringResource(R.string.settings_phone_hint), color = colors.textSecondary, fontSize = 14.sp)
            // Свой PIN-код приложения на этом телефоне.
            var pinOn by rememberSaveable { mutableStateOf(QuoteStore(context).pinSet) }
            var pinStage by remember { mutableStateOf<String?>(null) } // "new", "off"
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.pin_setting), color = colors.textPrimary, fontSize = 16.sp)
                    Text(stringResource(R.string.pin_setting_hint), color = colors.textSecondary, fontSize = 13.sp)
                }
                Switch(
                    checked = pinOn,
                    onCheckedChange = { v -> pinStage = if (v) "new" else "off" },
                    modifier = Modifier.semantics { contentDescription = "PIN-код приложения" },
                    colors = SwitchDefaults.colors(checkedTrackColor = colors.equalsKey, checkedThumbColor = colors.equalsKeyText),
                )
            }
            pinStage?.let { stage ->
                PinDialog(
                    turnOff = stage == "off",
                    onDone = { set ->
                        pinStage = null
                        if (set != null) {
                            pinOn = set
                            message = context.getString(if (set) R.string.pin_saved else R.string.pin_off)
                        }
                    },
                )
            }
            // Полный экран — каждый выбирает для своего телефона.
            run {
                var full by rememberSaveable { mutableStateOf(QuoteStore(context).fullScreen) }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(stringResource(R.string.full_screen_setting), color = colors.textPrimary, fontSize = 16.sp)
                        Text(stringResource(R.string.full_screen_hint), color = colors.textSecondary, fontSize = 13.sp)
                    }
                    Switch(
                        checked = full,
                        onCheckedChange = { v ->
                            full = v
                            QuoteStore(context).fullScreen = v
                            (context as? android.app.Activity)?.let { com.knit.calculator.applyFullScreen(it) }
                        },
                        colors = SwitchDefaults.colors(checkedTrackColor = colors.equalsKey, checkedThumbColor = colors.equalsKeyText),
                    )
                }
            }
            // Запрет снимков экрана — меняет только директор.
            if (director) {
                var secure by rememberSaveable { mutableStateOf(QuoteStore(context).secureScreen) }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(stringResource(R.string.secure_setting), color = colors.textPrimary, fontSize = 16.sp)
                        Text(stringResource(R.string.secure_setting_hint), color = colors.textSecondary, fontSize = 13.sp)
                    }
                    Switch(
                        checked = secure,
                        onCheckedChange = { v ->
                            secure = v
                            QuoteStore(context).secureScreen = v
                            (context as? android.app.Activity)?.let { com.knit.calculator.applySecureScreen(it) }
                        },
                        colors = SwitchDefaults.colors(checkedTrackColor = colors.equalsKey, checkedThumbColor = colors.equalsKeyText),
                    )
                }
            }
            // Вход по отпечатку / PIN телефона.
            val lockAvailable = remember { com.knit.calculator.ui.AppLock.available(context) }
            var lock by rememberSaveable { mutableStateOf(QuoteStore(context).appLock) }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.lock_setting), color = colors.textPrimary, fontSize = 16.sp)
                    Text(
                        stringResource(if (lockAvailable) R.string.lock_setting_hint else R.string.lock_unavailable),
                        color = colors.textSecondary, fontSize = 13.sp,
                    )
                }
                Switch(
                    checked = lock,
                    enabled = lockAvailable || lock,
                    onCheckedChange = { v ->
                        lock = v
                        QuoteStore(context).appLock = v
                    },
                    colors = SwitchDefaults.colors(checkedTrackColor = colors.equalsKey, checkedThumbColor = colors.equalsKeyText),
                )
            }
            message?.let { Text(it, color = colors.textPrimary, fontWeight = FontWeight.SemiBold, fontSize = 14.sp) }
            }
            2 -> {
            SectionTitle(R.string.company_section_requisites)
            ReadOnlyHint(config.enabled, editable)
            KnitField(s.brand, { v -> set { it.copy(brand = v) } }, R.string.company_brand, enabled = editable, text = true, maxLength = 100)
            KnitField(s.legalName, { v -> set { it.copy(legalName = v) } }, R.string.company_legal, enabled = editable, text = true, maxLength = 100)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                KnitField(s.inn, { v -> set { it.copy(inn = v.filter(Char::isDigit).take(12)) } }, R.string.company_inn, enabled = editable, keyboardType = KeyboardType.Number, text = true, modifier = Modifier.weight(1f))
                KnitField(s.city, { v -> set { it.copy(city = v) } }, R.string.company_city, enabled = editable, text = true, modifier = Modifier.weight(1f))
            }
            KnitField(s.phone, { v -> set { it.copy(phone = v) } }, R.string.company_phone, enabled = editable, text = true, keyboardType = KeyboardType.Phone)
            KnitField(s.email, { v -> set { it.copy(email = v.trim()) } }, R.string.company_email, enabled = editable, text = true, keyboardType = KeyboardType.Email, maxLength = 100)
            KnitField(s.website, { v -> set { it.copy(website = v.trim()) } }, R.string.company_site, enabled = editable, text = true, keyboardType = KeyboardType.Uri, maxLength = 100)
            KnitField(s.legalFullName, { v -> set { it.copy(legalFullName = v) } }, R.string.company_legal_full, enabled = editable, text = true, singleLine = false, maxLength = 200)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                KnitField(s.kpp, { v -> set { it.copy(kpp = v) } }, R.string.company_kpp, enabled = editable, text = true, keyboardType = KeyboardType.Number, maxLength = 25, modifier = Modifier.weight(1f))
                KnitField(s.ogrn, { v -> set { it.copy(ogrn = v) } }, R.string.company_ogrn, enabled = editable, text = true, keyboardType = KeyboardType.Number, maxLength = 25, modifier = Modifier.weight(1f))
            }
            KnitField(s.legalAddress, { v -> set { it.copy(legalAddress = v) } }, R.string.company_legal_address, enabled = editable, text = true, singleLine = false, maxLength = 200)
            KnitField(s.factAddress, { v -> set { it.copy(factAddress = v) } }, R.string.company_fact_address, enabled = editable, text = true, singleLine = false, maxLength = 200)
            KnitField(s.director, { v -> set { it.copy(director = v) } }, R.string.company_director, enabled = editable, text = true, maxLength = 100)
            KnitField(s.bank, { v -> set { it.copy(bank = v) } }, R.string.company_bank, enabled = editable, text = true, maxLength = 100)
            KnitField(s.account, { v -> set { it.copy(account = v) } }, R.string.company_account, enabled = editable, text = true, keyboardType = KeyboardType.Number, maxLength = 25)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                KnitField(s.bik, { v -> set { it.copy(bik = v) } }, R.string.company_bik, enabled = editable, text = true, keyboardType = KeyboardType.Number, maxLength = 25, modifier = Modifier.weight(1f))
                KnitField(s.corrAccount, { v -> set { it.copy(corrAccount = v) } }, R.string.company_corr, enabled = editable, text = true, keyboardType = KeyboardType.Number, maxLength = 25, modifier = Modifier.weight(1f))
            }
            }
            3 -> {
            SectionTitle(R.string.company_section_terms)
            ReadOnlyHint(config.enabled, editable)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                KnitField(s.vatRate, { v -> set { it.copy(vatRate = v) } }, R.string.company_vat_rate, enabled = editable, modifier = Modifier.weight(1f))
                KnitField(s.validityDays, { v -> set { it.copy(validityDays = v.filter(Char::isDigit).take(3)) } }, R.string.company_validity, enabled = editable, modifier = Modifier.weight(1f))
            }
            KnitField(s.prepayPercent, { v -> set { it.copy(prepayPercent = v) } }, R.string.company_prepay, enabled = editable)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.company_vat_included), color = colors.textPrimary, fontSize = 16.sp)
                    Text(stringResource(R.string.company_vat_included_hint), color = colors.textSecondary, fontSize = 13.sp)
                }
                Switch(
                    enabled = editable,
                    checked = s.vatIncluded,
                    onCheckedChange = { v -> set { it.copy(vatIncluded = v) } },
                    colors = SwitchDefaults.colors(
                        checkedTrackColor = colors.equalsKey,
                        checkedThumbColor = colors.equalsKeyText,
                    ),
                )
            }
            KnitField(s.leadTime, { v -> set { it.copy(leadTime = v) } }, R.string.company_lead_time, enabled = editable, text = true, maxLength = 100)
            KnitField(s.freeDeliveryFrom, { v -> set { it.copy(freeDeliveryFrom = v) } }, R.string.company_free_delivery, enabled = editable)
            KnitField(s.deliveryArea, { v -> set { it.copy(deliveryArea = v) } }, R.string.company_delivery_area, enabled = editable, text = true, maxLength = 150)
            KnitField(s.terms, { v -> set { it.copy(terms = v) } }, R.string.company_terms, enabled = editable, text = true, singleLine = false, maxLength = 500)
            KnitField(s.signature, { v -> set { it.copy(signature = v) } }, R.string.company_signature, enabled = editable, text = true, maxLength = 120)
            KnitField(s.shopUrl, { v -> set { it.copy(shopUrl = v.trim()) } }, R.string.company_shop, enabled = editable, text = true, keyboardType = KeyboardType.Uri, maxLength = 200)
            }
            4 -> {
            SectionTitle(R.string.company_section_legal)
            ReadOnlyHint(config.enabled, editable)
            KnitField(s.termOffer, { v -> set { it.copy(termOffer = v) } }, R.string.company_term_offer, enabled = editable, text = true, singleLine = false, maxLength = 600)
            KnitField(s.termPayment, { v -> set { it.copy(termPayment = v) } }, R.string.company_term_payment, enabled = editable, text = true, singleLine = false, maxLength = 600)
            KnitField(s.termQuality, { v -> set { it.copy(termQuality = v) } }, R.string.company_term_quality, enabled = editable, text = true, singleLine = false, maxLength = 600)
            KnitField(s.termRights, { v -> set { it.copy(termRights = v) } }, R.string.company_term_rights, enabled = editable, text = true, singleLine = false, maxLength = 600)
            KnitField(s.termConfidential, { v -> set { it.copy(termConfidential = v) } }, R.string.company_term_confidential, enabled = editable, text = true, singleLine = false, maxLength = 600)
            KnitField(s.termPersonal, { v -> set { it.copy(termPersonal = v) } }, R.string.company_term_personal, enabled = editable, text = true, singleLine = false, maxLength = 600)
            KnitField(s.emailDisclaimer, { v -> set { it.copy(emailDisclaimer = v) } }, R.string.company_email_disclaimer, enabled = editable, text = true, singleLine = false, maxLength = 600)
            }
            else -> {
            SectionTitle(R.string.company_section_role)
            DirectorSection(viewModel, s, director, editable)

            if (director) SectionTitle(R.string.company_section_economics)
            if (director) ReadOnlyHint(config.enabled, editable)
            if (director) Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                KnitField(s.fixedMonthly, { v -> set { it.copy(fixedMonthly = v) } }, R.string.company_fixed, enabled = editable, modifier = Modifier.weight(1f))
                KnitField(s.planQuantity, { v -> set { it.copy(planQuantity = v) } }, R.string.company_plan, enabled = editable, modifier = Modifier.weight(1f))
            }
            if (director) Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                KnitField(s.commissionPercent, { v -> set { it.copy(commissionPercent = v) } }, R.string.company_commission, enabled = editable, modifier = Modifier.weight(1f))
                KnitField(s.targetMarginPercent, { v -> set { it.copy(targetMarginPercent = v) } }, R.string.company_target, enabled = editable, modifier = Modifier.weight(1f))
            }
            if (director) Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                KnitField(s.maxDiscountPercent, { v -> set { it.copy(maxDiscountPercent = v) } }, R.string.company_max_discount, enabled = editable, modifier = Modifier.weight(1f))
                KnitField(s.reminderDays, { v -> set { it.copy(reminderDays = v) } }, R.string.company_reminder, enabled = editable, modifier = Modifier.weight(1f))
            }
            if (director) KnitField(s.yarnWastePercent, { v -> set { it.copy(yarnWastePercent = v) } }, R.string.company_yarn_waste, enabled = editable)
            if (editable && director) {
                TextButton(onClick = viewModel::resetSettings, modifier = Modifier.align(Alignment.CenterHorizontally)) {
                    Text(stringResource(R.string.company_reset), color = colors.textSecondary)
                }
            }
            }
        }
    }
}

/** Почему поля только для чтения: значения из таблицы или правит только директор. */
@Composable
private fun ReadOnlyHint(sheet: Boolean, editable: Boolean) {
    if (editable) return
    Text(
        stringResource(if (sheet) R.string.company_from_sheet else R.string.company_director_only),
        color = LocalKnitColors.current.textSecondary, fontSize = 14.sp,
    )
}

/** Режим директора: вход по PIN, выход; без таблицы PIN задаётся здесь. */
@Composable
private fun DirectorSection(viewModel: QuoteViewModel, s: CompanySettings, director: Boolean, editable: Boolean) {
    val colors = LocalKnitColors.current
    val context = LocalContext.current
    var pin by rememberSaveable { mutableStateOf("") }
    val wrongPin = stringResource(R.string.director_wrong_pin)
    when {
        s.directorPin.isBlank() -> Text(stringResource(R.string.director_no_pin), color = colors.textSecondary, fontSize = 14.sp)
        director -> {
            Text(stringResource(R.string.director_on), color = colors.textSecondary, fontSize = 14.sp)
            ActionButton(R.string.director_logout, R.drawable.ic_lock, primary = false, Modifier.fillMaxWidth()) { viewModel.lockDirector() }
        }
        else -> {
            Text(stringResource(R.string.director_off), color = colors.textSecondary, fontSize = 14.sp)
            KnitField(pin, { pin = it.filter(Char::isDigit).take(8) }, R.string.director_pin, text = true, keyboardType = KeyboardType.NumberPassword, maxLength = 8)
            ActionButton(R.string.director_login, R.drawable.ic_lock, primary = true, Modifier.fillMaxWidth()) {
                if (viewModel.unlockDirector(pin)) pin = "" else android.widget.Toast.makeText(context, wrongPin, android.widget.Toast.LENGTH_SHORT).show()
            }
        }
    }
    // Без таблицы PIN задаёт директор на своём телефоне; с таблицей — строка «PIN директора» в листе «Настройки».
    if (editable) {
        KnitField(s.directorPin, { v -> viewModel.updateSettings { it.copy(directorPin = v.filter(Char::isDigit).take(8)) } }, R.string.director_set_pin, text = true, keyboardType = KeyboardType.NumberPassword, maxLength = 8)
    }
}

/** Открывает ссылку (Google Таблицу) в браузере или приложении «Таблицы». */
fun openUrl(context: android.content.Context, url: String) {
    if (url.isBlank()) return
    try {
        context.startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(url)))
    } catch (e: android.content.ActivityNotFoundException) {
        android.widget.Toast.makeText(context, R.string.yarn_no_app, android.widget.Toast.LENGTH_LONG).show()
    }
}

/** Установка своего PIN (дважды) или отключение (текущим PIN). [onDone]: true — задан, false — снят, null — отмена. */
@Composable
private fun PinDialog(turnOff: Boolean, onDone: (Boolean?) -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val store = remember { QuoteStore(context) }
    val colors = LocalKnitColors.current
    var first by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    // Пауза после 5 ошибок действует и здесь: закрыть и открыть диалог заново её не снимает.
    var attempts by remember { mutableStateOf(store.pinAttempts) }
    var now by remember { mutableStateOf(System.currentTimeMillis()) }
    val paused = turnOff && attempts.blocked(now)
    androidx.compose.runtime.LaunchedEffect(attempts.blockedUntil) {
        while (System.currentTimeMillis() < attempts.blockedUntil) {
            now = System.currentTimeMillis()
            kotlinx.coroutines.delay(500)
        }
        now = System.currentTimeMillis()
    }
    val mismatch = stringResource(R.string.pin_mismatch)
    val wrong = stringResource(R.string.pin_wrong)
    androidx.compose.ui.window.Dialog(
        onDismissRequest = { onDone(null) },
        properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Column(
            Modifier.fillMaxSize().background(colors.background).padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            val title = when {
                turnOff -> stringResource(R.string.pin_enter)
                first == null -> stringResource(R.string.pin_new)
                else -> stringResource(R.string.pin_repeat)
            }
            com.knit.calculator.ui.PinPad(
                title,
                if (paused) stringResource(R.string.pin_paused, ((attempts.blockedUntil - now + 999) / 1000).toInt()) else error,
                enabled = !paused,
            ) { pin ->
                when {
                    turnOff -> if (store.checkPin(pin)) {
                        store.pinAttempts = com.knit.calculator.core.PinLock.Attempts()
                        store.setPin(null)
                        onDone(false)
                    } else {
                        val next = store.pinAttempts.failed(System.currentTimeMillis())
                        store.pinAttempts = next
                        attempts = next
                        now = System.currentTimeMillis()
                        error = if (next.blocked(now)) null else wrong.format(next.left())
                    }
                    first == null -> {
                        val problem = com.knit.calculator.core.PinLock.problem(pin)
                        if (problem != null) error = problem else {
                            first = pin
                            error = null
                        }
                    }
                    pin == first -> {
                        store.setPin(pin)
                        onDone(true)
                    }
                    else -> {
                        first = null
                        error = mismatch
                    }
                }
            }
            androidx.compose.material3.TextButton(onClick = { onDone(null) }) {
                Text(stringResource(R.string.cancel), color = colors.textSecondary)
            }
        }
    }
}
