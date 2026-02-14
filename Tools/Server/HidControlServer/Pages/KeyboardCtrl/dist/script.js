// caps lock key
document.addEventListener("DOMContentLoaded", function() {
	const capsLock = document.getElementById("caps-lock");
	capsLock?.addEventListener("click", function() {
		const pressed = this.ariaPressed === "true";
		this.setAttribute("aria-pressed",!pressed);
	});
});