// knitERP, CRM (D693): диктовка заметки через Web Speech API браузера (SpeechRecognition / webkitSpeechRecognition), язык ru-RU.
// Модуль подключается из CrmVoiceNote.razor через IJSRuntime import. Наш код никуда не отправляет ни звук, ни текст: распознанный
// текст возвращается только в компонент на странице. Каким движком распознаёт браузер (на устройстве или своим облачным сервисом) —
// решение браузера, об этом сказано в подсказке у кнопки.
let current = null;

function engine() {
    return window.SpeechRecognition || window.webkitSpeechRecognition || null;
}

export function supported() {
    return engine() !== null;
}

// Начать диктовку. ref — DotNetObjectReference компонента: OnSpeech(итоговый, промежуточный), OnSpeechError(код), OnSpeechEnd().
export function start(ref, lang) {
    stop();
    const Engine = engine();
    if (!Engine) {
        return false;
    }

    const r = new Engine();
    r.lang = lang || 'ru-RU';
    r.interimResults = true;
    r.continuous = true;
    r.maxAlternatives = 1;
    let finalText = '';
    r.onresult = function (e) {
        let interim = '';
        for (let i = e.resultIndex; i < e.results.length; i++) {
            const text = e.results[i][0].transcript;
            if (e.results[i].isFinal) {
                finalText += text;
            } else {
                interim += text;
            }
        }
        ref.invokeMethodAsync('OnSpeech', finalText, interim).catch(function () { });
    };
    r.onerror = function (e) {
        ref.invokeMethodAsync('OnSpeechError', (e && e.error) || '').catch(function () { });
    };
    r.onend = function () {
        if (current === r) {
            current = null;
        }
        ref.invokeMethodAsync('OnSpeechEnd').catch(function () { });
    };
    current = r;
    try {
        r.start();
    } catch (err) {
        current = null;
        return false;
    }
    return true;
}

export function stop() {
    if (current) {
        const r = current;
        current = null;
        try {
            r.stop();
        } catch (err) {
            // уже остановлено
        }
    }
}
