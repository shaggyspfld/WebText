// Draws the authenticator QR code for any element with data-otpauth="otpauth://..." (uses lib/qrcode-generator).
(function () {
    document.querySelectorAll("[data-otpauth]").forEach(function (el) {
        try {
            var qr = qrcode(0, "M");
            qr.addData(el.getAttribute("data-otpauth"));
            qr.make();
            el.innerHTML = qr.createSvgTag({ cellSize: 4, margin: 2, scalable: true });
        } catch (e) {
            el.textContent = "The QR code couldn't be drawn. Type the key below into your app instead.";
        }
    });
})();
