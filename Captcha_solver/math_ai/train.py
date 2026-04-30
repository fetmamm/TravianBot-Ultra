import os
import shutil
from datetime import datetime

import tensorflow as tf
from tensorflow.keras import layers


dataset_path = "dataset"
image_size = (64, 64)
batch_size = 16
epochs = 10


def backup_existing_model() -> None:
    if not os.path.exists("model.keras"):
        return

    os.makedirs("model_backups", exist_ok=True)
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup_path = os.path.join("model_backups", f"model_{timestamp}.keras")
    shutil.copy2("model.keras", backup_path)
    print("Backup saved:", backup_path)


train_data = tf.keras.utils.image_dataset_from_directory(
    dataset_path,
    validation_split=0.2,
    subset="training",
    seed=123,
    image_size=image_size,
    color_mode="grayscale",
    batch_size=batch_size
)

val_data = tf.keras.utils.image_dataset_from_directory(
    dataset_path,
    validation_split=0.2,
    subset="validation",
    seed=123,
    image_size=image_size,
    color_mode="grayscale",
    batch_size=batch_size
)

class_names = train_data.class_names
print("Classes:", class_names)

model = tf.keras.Sequential([
    layers.Rescaling(1.0 / 255),

    layers.Conv2D(32, 3, activation="relu"),
    layers.MaxPooling2D(),

    layers.Conv2D(64, 3, activation="relu"),
    layers.MaxPooling2D(),

    layers.Flatten(),
    layers.Dense(64, activation="relu"),
    layers.Dense(len(class_names), activation="softmax")
])

model.compile(
    optimizer="adam",
    loss="sparse_categorical_crossentropy",
    metrics=["accuracy"]
)

model.fit(
    train_data,
    validation_data=val_data,
    epochs=epochs
)

backup_existing_model()
model.save("model.keras")

with open("classes.txt", "w", encoding="utf-8") as file:
    for name in class_names:
        file.write(name + "\n")

print("KLAR")
