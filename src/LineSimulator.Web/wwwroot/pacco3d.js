// Vista 3D del pacco della versione web: pacco con le legature e catena di quote (distanze tra le legature) sopra il pacco,
// come nel programma Windows. Assi: X lungo il pacco (testa a destra), Y in alto, Z in larghezza. Misure in metri.
(function () {
    'use strict';
    var P = null;   // stato corrente

    function css(name, fallback) {
        var v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return v || fallback;
    }

    function label(text, dark) {
        var c = document.createElement('canvas');
        c.width = 192; c.height = 80;
        var g = c.getContext('2d');
        g.fillStyle = dark ? 'rgba(31,39,48,0.92)' : 'rgba(255,255,255,0.92)';
        g.fillRect(0, 0, 192, 80);
        g.strokeStyle = dark ? '#38424e' : '#c9d0d8';
        g.strokeRect(1, 1, 190, 78);
        g.fillStyle = dark ? '#e6ebf0' : '#1b2430';
        g.font = '600 46px "Segoe UI", system-ui, sans-serif';
        g.textAlign = 'center';
        g.textBaseline = 'middle';
        g.fillText(text, 96, 42);
        var s = new THREE.Sprite(new THREE.SpriteMaterial({ map: new THREE.CanvasTexture(c), depthTest: false }));
        s.scale.set(0.62, 0.26, 1);
        s.renderOrder = 10;
        return s;
    }

    function box(sx, sy, sz, x, y, z, mat) {
        var m = new THREE.Mesh(new THREE.BoxGeometry(sx, sy, sz), mat);
        m.position.set(x, y, z);
        return m;
    }

    function dispose() {
        if (!P) return;
        P.alive = false;
        if (P.observer) P.observer.disconnect();
        P.canvas.removeEventListener('pointerdown', P.onDown);
        P.canvas.removeEventListener('pointermove', P.onMove);
        P.canvas.removeEventListener('pointerup', P.onUp);
        P.canvas.removeEventListener('wheel', P.onWheel);
        P.renderer.dispose();
        P = null;
    }

    function place() {
        var o = P.orbit, c = P.cam;
        o.phi = Math.min(1.5, Math.max(0.05, o.phi));
        c.position.set(
            o.target.x + o.radius * Math.sin(o.phi) * Math.cos(o.theta),
            o.target.y + o.radius * Math.cos(o.phi),
            o.target.z + o.radius * Math.sin(o.phi) * Math.sin(o.theta));
        c.lookAt(o.target);
        P.need = true;
    }

    function resize() {
        var w = P.canvas.clientWidth, h = P.canvas.clientHeight;
        if (w < 2 || h < 2) return;
        P.renderer.setSize(w, h, false);
        P.cam.aspect = w / h;
        P.cam.updateProjectionMatrix();
        P.need = true;
    }

    function fitRadius() {
        // distanza con cui il pacco e le quote stanno nell'inquadratura
        var half = P.half, aspect = P.cam.aspect || 1.6;
        var vf = P.cam.fov * Math.PI / 180;
        var needW = half * 2.25 / aspect, needH = P.height * 1.25;
        return Math.max(needW, needH) / (2 * Math.tan(vf / 2)) + 0.6;
    }

    function loop() {
        if (!P || !P.alive) return;
        if (P.need) { P.need = false; P.renderer.render(P.scene, P.cam); }
        requestAnimationFrame(loop);
    }

    function build(d, dark) {
        var scene = new THREE.Scene();
        scene.background = new THREE.Color(dark ? 0x0b1016 : 0xf4f6f8);
        scene.add(new THREE.AmbientLight(0xffffff, 0.85));
        var sun = new THREE.DirectionalLight(0xffffff, 0.6);
        sun.position.set(3, 8, 5);
        scene.add(sun);

        var L = d.lengthM, W = d.widthM, H = d.heightM;
        var packMat = new THREE.MeshLambertMaterial({ color: dark ? 0x6b7c8e : 0x9aa7b4 });
        var edge = new THREE.LineSegments(new THREE.EdgesGeometry(new THREE.BoxGeometry(L, H, W)),
            new THREE.LineBasicMaterial({ color: dark ? 0xb8c4d0 : 0x4a5563 }));
        var pack = box(L, H, W, 0, H / 2, 0, packMat);
        scene.add(pack);
        edge.position.set(0, H / 2, 0);
        scene.add(edge);

        var strapMat = new THREE.MeshLambertMaterial({ color: dark ? 0xf2a93b : 0xd97706 });
        var dimColor = dark ? 0xaab6c2 : 0x34465c;
        var dimMat = new THREE.MeshBasicMaterial({ color: dimColor });
        var pos = d.straps.slice().sort(function (a, b) { return a - b; });
        pos.forEach(function (m) {
            scene.add(box(0.05, H + 0.03, W + 0.03, L / 2 - m, H / 2, 0, strapMat));
        });

        // catena di quote sopra il pacco: testa → legature → coda, richiami verticali
        if (pos.length) {
            var y = H + 0.55, marks = [0].concat(pos, [L]);
            scene.add(box(L, 0.012, 0.012, 0, y, 0, dimMat));
            marks.forEach(function (m) {
                var x = L / 2 - m;
                scene.add(box(0.012, 0.16, 0.012, x, y, 0, dimMat));
                scene.add(box(0.008, y - H - 0.02, 0.008, x, (y + H) / 2, 0, dimMat));
            });
            for (var i = 1; i < marks.length; i++) {
                var a = marks[i - 1], b = marks[i];
                if (b - a < 1e-6) continue;
                var s = label(String(Math.round((b - a) * 1000)), dark);
                s.position.set(L / 2 - (a + b) / 2, y + 0.2, 0);
                scene.add(s);
            }
        }
        // piano d'appoggio
        scene.add(box(L + 1.2, 0.02, W + 1.0, 0, -0.011, 0, new THREE.MeshLambertMaterial({ color: dark ? 0x1a222b : 0xdfe4ea })));
        return scene;
    }

    window.pacco3d = {
        // d = { lengthM, widthM, heightM, straps: [m dalla testa], dark }
        show: function (canvasId, d) {
            try {
                var canvas = document.getElementById(canvasId);
                if (!canvas || !window.THREE) return false;
                var keep = P && P.canvas === canvas ? { theta: P.orbit.theta, phi: P.orbit.phi, user: P.user } : null;
                dispose();
                var renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true });
                renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
                P = {
                    alive: true, canvas: canvas, renderer: renderer, need: true, half: d.lengthM / 2,
                    height: d.heightM + 1.0, user: keep ? keep.user : false,
                    cam: new THREE.PerspectiveCamera(35, 1.6, 0.05, 200),
                    orbit: { theta: keep ? keep.theta : Math.PI * 0.12, phi: keep ? keep.phi : 1.18, radius: 8, target: new THREE.Vector3(0, (d.heightM + 0.5) / 2, 0) }
                };
                var t = document.documentElement.getAttribute('data-tema');
                var dark = t === 'scuro' || (t !== 'chiaro' && window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches);
                P.scene = build(d, dark);
                var down = null;
                P.onDown = function (e) { down = { x: e.clientX, y: e.clientY }; canvas.setPointerCapture(e.pointerId); };
                P.onMove = function (e) {
                    if (!down) return;
                    P.orbit.theta -= (e.clientX - down.x) * 0.008;
                    P.orbit.phi -= (e.clientY - down.y) * 0.008;
                    down = { x: e.clientX, y: e.clientY };
                    P.user = true;
                    place();
                };
                P.onUp = function () { down = null; };
                P.onWheel = function (e) { e.preventDefault(); P.orbit.radius *= e.deltaY > 0 ? 1.1 : 0.9; place(); };
                canvas.addEventListener('pointerdown', P.onDown);
                canvas.addEventListener('pointermove', P.onMove);
                canvas.addEventListener('pointerup', P.onUp);
                canvas.addEventListener('wheel', P.onWheel, { passive: false });
                P.observer = new ResizeObserver(function () { if (P) { resize(); P.orbit.radius = fitRadius(); place(); } });
                P.observer.observe(canvas);
                resize();
                P.orbit.radius = fitRadius();
                place();
                loop();
                return true;
            } catch (e) { return false; }
        },
        view: function (name) {
            if (!P) return;
            if (name === 'alto') { P.orbit.theta = 0; P.orbit.phi = 0.05; }
            else if (name === 'lato') { P.orbit.theta = Math.PI / 2; P.orbit.phi = 1.5; }
            else { P.orbit.theta = Math.PI * 0.12; P.orbit.phi = 1.18; }
            P.orbit.radius = fitRadius();
            place();
        },
        dispose: dispose
    };
})();
