import * as mistralSdk from "@mistralai/mistralai";
import {getConfigVariable} from "./util.js";

export default class MistralService {
    #mistral;
    #model = "mistral-small-latest";

    constructor() {
        const apiKey = getConfigVariable("MISTRAL_API_KEY")

        const MistralConstructor = mistralSdk.MistralClient ?? mistralSdk.Mistral ?? mistralSdk.default;

        if (!MistralConstructor) {
            throw new Error("Unsupported @mistralai/mistralai package version: no client constructor export found.");
        }

        this.#mistral = this.#createClient(MistralConstructor, apiKey);
    }

    async classify(allLists, destinationName, description) {
        try {
            const prompt = `Categorize this transaction from my bank account with the following 
        description ${description} and the following destination ${destinationName}`;

            const categories = allLists.get('categories');
            const budgets = allLists.get('budgets');

            const request = {
                model: this.#model,
                messages: [{role: "user", content: prompt}],
                tools: [
                    {
                        "type": "function",
                        "function": {
                            "name": "classification",
                            "description": "Classify a financial transaction into a category and budget, use only values from the lists provided.",
                            "parameters": {
                                "type": "object",
                                "properties": {
                                    "category": {
                                        "type": "string",
                                        "description": `The category to classify the transaction into. 
                                    Use only these values: ${categories.join(", ")}.
                                    Use none if no category applies.`
                                    },
                                    "budget": {
                                        "type": "string",
                                        "description": `The budget to classify the transaction into.
                                    Use only these values: ${budgets.join(", ")}.
                                    Use none if no budget applies.
                                    `
                                    }
                                },
                                "required": ["category", "budget"]
                            }
                        }
                    }
                ],
                tool_choice: {"type": "function", "function": {"name": "classification"}},
            };

            const response = await this.#chat(request);
            const functionCall = response.choices?.[0]?.message?.tool_calls?.[0];

            if (!functionCall?.function?.arguments) {
                throw new MistralException(null, null, "Mistral did not return a tool call for classification.");
            }

            const json = JSON.parse(functionCall.function.arguments);

            if (categories.indexOf(json.category) === -1 && budgets.indexOf(json.budget) === -1) {
                console.warn(`Mistral could not classify the transaction. 
                Prompt: ${prompt}
                Mistral's guess: ${functionCall.function.arguments}`)
                return null;
            }

            return {
                prompt,
                response: functionCall.function.arguments,
                category: json.category,
                budget: json.budget
            }

        } catch (error) {
            if (error instanceof MistralException) {
                throw error;
            }

            if (error.response) {
                console.error(error.response.status);
                console.error(error.response.data);
                throw new MistralException(error.status, error.response, error.response.data);
            } else {
                console.error(error.message);
                throw new MistralException(null, null, error.message);
            }
        }
    }

    #createClient(MistralConstructor, apiKey) {
        try {
            return new MistralConstructor({apiKey});
        } catch {
            return new MistralConstructor(apiKey);
        }
    }

    async #chat(request) {
        if (typeof this.#mistral.chat === "function") {
            return await this.#mistral.chat(request);
        }

        if (this.#mistral.chat && typeof this.#mistral.chat.complete === "function") {
            return await this.#mistral.chat.complete(request);
        }

        throw new Error("Unsupported @mistralai/mistralai package version: no chat method found.");
    }
}

class MistralException extends Error {
    code;
    response;
    body;

    constructor(statusCode, response, body) {
        super(`Error while communicating with Mistral: ${statusCode} - ${body}`);

        this.code = statusCode;
        this.response = response;
        this.body = body;
    }
}
