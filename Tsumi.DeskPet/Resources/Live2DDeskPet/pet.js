// ---- Alife 窗口桥 ----
// 5.x 客户端用 WebView2 承载桌宠页面，宿主会在页面脚本执行前注入 window.alifeWindow。
// 保留原来的 postMessage / messageBus 接口，页面其余代码无需改动。

function postMessage(data) {
    try {
        if (window.alifeWindow) window.alifeWindow.postMessage(data);
    } catch {
    }
}

const messageBus = {
    _handlers: new Map(),
    on(type, handler) {
        if (!this._handlers.has(type)) {
            this._handlers.set(type, []);
        }

        this._handlers.get(type).push(handler);
    },
    _dispatch(msg) {
        const handlers = this._handlers.get(msg.type) || [];

        handlers.forEach(handler => handler(msg));
    }
};

if (window.alifeWindow) {
    window.alifeWindow.onMessage(function (msg) {
        if (typeof msg === 'string') {
            try {
                msg = JSON.parse(msg);
            } catch {
                return;
            }
        }
        messageBus._dispatch(msg);
    });
}


// ---- 前端日志转发 ----
function formatLogArg(arg) {
    if (arg instanceof Error) return arg.name + ': ' + arg.message + '\n' + (arg.stack || '');
    if (typeof arg === 'object') {
        try {
            return JSON.stringify(arg);
        } catch {
            return String(arg);
        }
    }
    return String(arg);
}

function postLog(level) {
    try {
        var args = Array.prototype.slice.call(arguments, 1);
        postMessage({
            type: 'log',
            level: level,
            text: args.map(formatLogArg).join(' ')
        });
    } catch {
    }
}

{
    var rawWarn = console.warn.bind(console);
    var rawError = console.error.bind(console);
    console.warn = function () {
        postLog.apply(null, ['warn'].concat(Array.prototype.slice.call(arguments)));
        rawWarn.apply(console, arguments);
    };
    console.error = function () {
        postLog.apply(null, ['error'].concat(Array.prototype.slice.call(arguments)));
        rawError.apply(console, arguments);
    };
}

window.addEventListener('error', function (e) {
    var target = e.target;
    if (target && target !== window) {
        postLog('resource-error', target.tagName || 'unknown', target.src || target.href || '');
        return;
    }
    postLog('error', e.message, e.filename + ':' + e.lineno + ':' + e.colno, e.error || '');
}, true);

window.addEventListener('unhandledrejection', function (e) {
    postLog('unhandledrejection', e.reason);
});

// ---- 前端资源注入辅助 ----
function injectCSS(css) {
    var style = document.createElement('style');
    style.textContent = css;
    document.head.appendChild(style);
}

function injectHTML(html) {
    var div = document.createElement('div');
    div.innerHTML = html;
    while (div.firstChild) document.body.appendChild(div.firstChild);
}

// ---- 加载模型 ----

messageBus.on('load', function (msg) {
    loadModel(msg.url, msg.pins, msg.scale, msg.offsetX, msg.offsetY, msg.heightRatio);
});

const app = new PIXI.Application({
    view: document.getElementById('canvas'),
    autoStart: true,
    resizeTo: window,
    transparent: true,
    backgroundAlpha: 0,
    // 按屏幕 DPI 渲染：不设的话画布按 1 倍像素画，再被系统放大到实际显示尺寸 → 整只模型发糊
    resolution: window.devicePixelRatio || 1,
    autoDensity: true,
    preserveDrawingBuffer: true, // 保留帧缓冲，允许读取像素（生成人形掩码）
});

let model = null;

// 用户在模型上的追加偏移与缩放（独立于布局基准，窗口 resize/updateLayout 不会重置）
let userScale = 1;
let userOffsetX = 0;
let userOffsetY = 0;

// 模型高度相对窗口高度的倍数（1.1 = 比窗口高一点）。由 MoveScaleModule 的滑块调整并持久化。
let modelHeightRatio = 1.1;

// 高度倍数改变（滑块实时调时也会走这里）：只重排，不写盘；save=true 才回报给主进程持久化
function applyHeightRatio(percent, save) {
    let ratio = Math.max(0.3, Math.min(2.5, (percent || 110) / 100));
    modelHeightRatio = ratio;
    const label = document.getElementById('pet-height-value');
    const range = document.getElementById('pet-height-range');
    if (label) label.textContent = Math.round(ratio * 100) + '%';
    if (range) range.value = String(Math.round(ratio * 100));
    applyTransform();
    if (save) postMessage({ type: 'pet_height_ratio', ratio: ratio });
}

// 当前模型视图（窗口 CSS 像素）：mx/my=模型中心位置，ms=每模型单位的像素数。
// 点击区域/穿透判定/配置面板靠它做「模型单位 ⇄ 窗口坐标」换算，与对话框 --ui-scale 同源。
var petView = { mx: 0, my: 0, ms: 1, dirty: false, sentMx: NaN, sentMy: NaN, sentMs: NaN };

// 视图变化时同步：置脏（节流上报主进程）+ 通知 overlay 图层贴模型（region 模块注入后生效）
function updatePetView() {
    if (!model) return;
    petView.mx = model.position.x;
    petView.my = model.position.y;
    petView.ms = model.scale.x || 1;
    petView.dirty = true;
    // 模型中心水平位置暴露给 CSS（气泡等 UI 用它贴住模型，而不是锚死窗口中心）
    document.documentElement.style.setProperty('--model-cx', petView.mx + 'px');
    if (typeof window.onPetViewChanged === 'function') window.onPetViewChanged();
}

// pet_view 节流上报：100ms 合并一次且值没变不发——拖动/缩放期间最多 10 条/秒，
// 避免高频 IPC 轰炸主进程（此前主进程在 pet_view 路径做重活导致整个 Alife 卡死）。
setInterval(function () {
    if (!petView.dirty) return;
    petView.dirty = false;
    if (petView.mx === petView.sentMx && petView.my === petView.sentMy && petView.ms === petView.sentMs) return;
    petView.sentMx = petView.mx; petView.sentMy = petView.my; petView.sentMs = petView.ms;
    postMessage({ type: 'pet_view', mx: petView.mx, my: petView.my, ms: petView.ms });
}, 100);

// 模块加载完成后调用：作废去重标记，强制下个周期重发一次当前视图
//（主进程模块订阅可能晚于最早一次上报，静息状态下没有这条就永远拿不到视图）
window.requestPetView = function () {
    petView.dirty = true;
    petView.sentMx = NaN; petView.sentMy = NaN; petView.sentMs = NaN;
};

// 模型缩放策略（原始设计）：模型高度 = 窗口高度 × 倍数（默认 1.1，可在前台滑块里调）。
// 窗口是主人：窗口一 resize（用户拉伸/代码调整），模型跟着缩放。
function applyTransform() {
    if (!model) return;
    var bh = model.internalModel.originalHeight; // 画布逻辑高
    var sc = (window.innerHeight * modelHeightRatio) / bh;    // 模型高 = 窗口高 × 倍数
    model.scale.set(sc * userScale);
    model.position.set(window.innerWidth * 0.5 + userOffsetX, window.innerHeight * 0.5 + userOffsetY);
    updatePetView();
}

// 每帧强制固定的模型参数（水印这类"开关参数"）：动作与表情每帧都会重写参数，
// 所以在 coreModel.update() 之前压一次才能稳定生效——否则一有动作水印就闪回来。
// 参数名兼容两种大小写（桥接用 Newtonsoft 默认契约，记录类型会发成 Id/Value）。
function applyParamPins(pins) {
    if (!model || !model.internalModel || !pins || pins.length === 0) return;
    var core = model.internalModel.coreModel;
    if (!core || core.__tsumiPinsInstalled) return;

    var list = [];
    for (var i = 0; i < pins.length; i++) {
        var id = pins[i].id !== undefined ? pins[i].id : pins[i].Id;
        var value = pins[i].value !== undefined ? pins[i].value : pins[i].Value;
        var auto = pins[i].auto !== undefined ? pins[i].auto : (pins[i].Auto !== undefined ? pins[i].Auto : false);
        if (id !== undefined && id !== null) list.push({id: id, value: value, auto: !!auto});
    }
    if (list.length === 0) {
        console.warn('[Pet] 收到固定参数但解析不出参数名:', JSON.stringify(pins));
        return;
    }
    core.__tsumiPinsInstalled = true;

    // 决定每个参数最终固定到的值：模型自带水印参数固定到参数上限——
    // 有些模型的水印要“调大”才彻底消失，照表情里写的 1.0 固定只到一半；
    // 手动配置的参数则严格按用户给的数值固定。
    var probe = [];
    for (var n = 0; n < list.length; n++) {
        var p = list[n];
        p.target = p.value;
        try {
            var idx = core.getParameterIndex(p.id);
            if (idx >= 0) {
                p.min = core.getParameterMinimumValue(idx);
                p.max = core.getParameterMaximumValue(idx);
                p.def = core.getParameterDefaultValue(idx);
                if (p.auto && typeof p.max === 'number' && isFinite(p.max)) p.target = p.max;
            }
        } catch (e) { }
        probe.push(p.id + ' 范围=' + p.min + '~' + p.max + ' 默认=' + p.def +
            ' 现值=' + core.getParameterValueById(p.id) + ' → 固定=' + p.target);
    }
    console.warn('[Pet] 固定参数就位: ' + probe.join('; '));

    var rawUpdate = core.update.bind(core);
    var rawLoadParameters = core.loadParameters.bind(core);

    function writePins() {
        for (var k = 0; k < list.length; k++) {
            try {
                core.setParameterValueById(list[k].id, list[k].target);
            } catch (e) {
                console.warn('[Pet] 固定参数写入失败:', list[k], e);
            }
        }
    }

    // 渲染库每帧的顺序是 motion → saveParameters → 表情/物理 → update() → loadParameters()。
    // loadParameters() 会把参数恢复成动作保存的旧值，所以只包 update() 会在同一帧被还原掉；
    // 必须在 loadParameters() 之后再压一次，固定值才能稳定留到下一帧渲染。
    core.update = function () {
        writePins();
        return rawUpdate();
    };
    core.loadParameters = function () {
        var result = rawLoadParameters();
        writePins();
        return result;
    };
}

async function loadModel(url, pins, loadScale, loadOffsetX, loadOffsetY, loadHeightRatio) {
    console.warn('[Pet] pet.js 4.5.0 已加载；本次固定参数 = ' + JSON.stringify(pins || []));
    console.log('[Pet] Loading model:', url);
    if (model) app.stage.removeChild(model);
    // 套用主进程随 load 带来的持久化缩放/偏移/高度倍数：模型渲染前就定好基准，
    // 首帧即最终尺寸，避免先按 JS 默认高度倍数(1.1)画一帧、再被持久化值覆盖 → 重载闪一下。
    if (typeof loadScale === 'number' && loadScale > 0) userScale = loadScale;
    if (typeof loadOffsetX === 'number') userOffsetX = loadOffsetX;
    if (typeof loadOffsetY === 'number') userOffsetY = loadOffsetY;
    if (typeof loadHeightRatio === 'number' && loadHeightRatio > 0) modelHeightRatio = loadHeightRatio;
    try {
        model = await PIXI.live2d.Live2DModel.from(url, {autoInteract: false});
        console.log('[Pet] Model loaded successfully');
    } catch (err) {
        console.error('[Pet] Live2D model load failed:', err);
        postMessage({type: 'loaded'});
        return;
    }
    app.stage.addChild(model);
    applyParamPins(pins);
    
    var updateLayout = function () {
        var s = window.innerHeight / 540;
        document.documentElement.style.setProperty('--ui-scale', s);
        applyTransform();
    };

    model.anchor.set(0.5, 0.5);
    updateLayout();
    model.interactive = true;
    window.addEventListener('resize', updateLayout);

    // 渲染诊断：画布像素 vs 窗口尺寸能看出像素密度够不够（画布 = 窗口 × DPR 才算按屏幕原生像素渲染）
    console.warn('[Pet] 渲染诊断 DPR=' + window.devicePixelRatio
        + ' 窗口=' + window.innerWidth + 'x' + window.innerHeight
        + ' 画布像素=' + app.renderer.width + 'x' + app.renderer.height
        + ' 模型原始高=' + model.internalModel.originalHeight.toFixed(3)
        + ' 当前缩放=' + model.scale.y.toFixed(2)
        + ' 高度倍数=' + modelHeightRatio);

    // 上报模型原始尺寸（逻辑单位），供主进程自动贴合窗口：宽 = 当前高 × 宽高比
    postMessage({
        type: 'model_info',
        ow: model.internalModel.originalWidth,
        oh: model.internalModel.originalHeight
    });
    postMessage({type: 'loaded'});
}

// ---- 初始化完成 ----
postMessage({type: 'init'});
