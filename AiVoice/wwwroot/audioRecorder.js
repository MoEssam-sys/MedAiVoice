let mediaRecorder;
let audioChunks = [];

window.audioRecorder = {
    start: async function () {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        mediaRecorder = new MediaRecorder(stream);
        audioChunks = [];
        mediaRecorder.ondataavailable = e => audioChunks.push(e.data);
        mediaRecorder.start();
    },
    stop: function () {
        return new Promise(resolve => {
            mediaRecorder.onstop = () => {
                const audioBlob = new Blob(audioChunks, { type: 'audio/webm' });
                const reader = new FileReader();
                reader.readAsDataURL(audioBlob);
                reader.onloadend = () => {
                    const base64data = reader.result;
                    // إرجاع النص بصيغة Base64 إلى Blazor
                    resolve(base64data.split(',')[1]);
                }
            };
            mediaRecorder.stop();
            // إغلاق المايكروفون بعد الانتهاء
            mediaRecorder.stream.getTracks().forEach(t => t.stop());
        });
    }
};