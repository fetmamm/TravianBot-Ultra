import cv2
import numpy as np
import tensorflow as tf
import sympy as sp
import os


model = tf.keras.models.load_model("model.keras")

with open("classes.txt", "r", encoding="utf-8") as file:
    class_names = [line.strip() for line in file.readlines()]

folder = "test_images"
debug_dir = "debug_output"

os.makedirs(debug_dir, exist_ok=True)


for filename in os.listdir(folder):
    if not filename.lower().endswith((".png", ".jpg", ".jpeg")):
        continue

    path = os.path.join(folder, filename)

    safe_name = os.path.splitext(filename)[0]
    safe_name = safe_name.replace(" ", "_").replace("(", "").replace(")", "")

    img = cv2.imread(path)

    if img is None:
        print("Could not read image, skipping:", filename)
        continue

    gray_full = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)

    mask = cv2.inRange(gray_full, 220, 245)

    contours, _ = cv2.findContours(
        mask,
        cv2.RETR_EXTERNAL,
        cv2.CHAIN_APPROX_SIMPLE
    )

    candidate_boxes = []

    for c in contours:
        x, y, w, h = cv2.boundingRect(c)
        area = w * h

        if area > 2000 and w > 80 and h > 30:
            candidate_boxes.append((x, y, w, h))

    if candidate_boxes:
        x, y, w, h = max(candidate_boxes, key=lambda b: b[2] * b[3])
        crop = img[y:y+h, x:x+w]
    else:
        crop = img.copy()

    if crop.shape[0] > 180 or crop.shape[1] > 350:
        ch, cw, _ = crop.shape
        crop = crop[int(ch * 0.34):int(ch * 0.50), int(cw * 0.30):int(cw * 0.70)]

    cv2.imwrite(f"{debug_dir}/{safe_name}_crop.png", crop)

    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)

    _, thresh = cv2.threshold(
        gray,
        0,
        255,
        cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU
    )

    cv2.imwrite(f"{debug_dir}/{safe_name}_thresh.png", thresh)

    contours, _ = cv2.findContours(
        thresh,
        cv2.RETR_EXTERNAL,
        cv2.CHAIN_APPROX_SIMPLE
    )

    boxes = []

    for c in contours:
        x, y, w, h = cv2.boundingRect(c)

        if y < 20 or y > 90:
            continue

        if w < 4 or h < 6:
            continue

        if h <= 4:
            continue

        boxes.append((x, y, w, h))

    boxes = sorted(boxes, key=lambda b: b[0])

    filtered_boxes = []

    for x, y, w, h in boxes:
        if h <= 6 and w >= 15:
            break

        filtered_boxes.append((x, y, w, h))

    boxes = filtered_boxes

    expression = ""
    previous_right = None

    for x, y, w, h in boxes:
        if previous_right is not None:
            has_operator = "+" in expression or "-" in expression
            parts = expression.replace("-", "+").split("+")
            second_number_started = len(parts) >= 2 and parts[1].isdigit()

            if has_operator and second_number_started and x - previous_right > 20:
                break

        char_img = thresh[y:y+h, x:x+w]

        char_img = cv2.resize(
            char_img,
            None,
            fx=5,
            fy=5,
            interpolation=cv2.INTER_NEAREST
        )

        padded = cv2.copyMakeBorder(
            char_img,
            20, 20, 20, 20,
            cv2.BORDER_CONSTANT,
            value=0
        )

        resized = cv2.resize(
            padded,
            (64, 64),
            interpolation=cv2.INTER_NEAREST
        )

        cv2.imwrite(f"{debug_dir}/{safe_name}_char_{x}.png", resized)

        input_img = resized.astype("float32")
        input_img = np.expand_dims(input_img, axis=-1)
        input_img = np.expand_dims(input_img, axis=0)

        prediction = model.predict(input_img, verbose=0)

        index = np.argmax(prediction[0])
        symbol = class_names[index]

        if symbol == "plus":
            if expression and expression[-1].isdigit():
                expression += "+"
        elif symbol == "minus":
            if expression and expression[-1].isdigit():
                expression += "-"
        else:
            expression += symbol

        previous_right = x + w

    result_text = "Could not solve"
    try:
        result = sp.sympify(expression)
        result_text = str(result)
    except Exception:
        pass

    print("------------------------------")
    print(f"Filename: {filename}")
    print(f"Equation: {expression} , Results: {result_text}")
    print("------------------------------")
    print()
